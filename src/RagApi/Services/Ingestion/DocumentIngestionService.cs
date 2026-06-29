using System.Text.Json;
using Microsoft.Extensions.Options;
using RagApi.Data;
using RagApi.Models;
using RagApi.Options;

namespace RagApi.Services.Ingestion;

public class DocumentIngestionService
{
    private readonly DocumentParser _parser;
    private readonly ITeiEmbeddingService _embeddingService;
    private readonly ISqlDocumentRepository _repository;
    private readonly IngestionOptions _options;
    private readonly ILogger<DocumentIngestionService> _logger;

    public DocumentIngestionService(
        DocumentParser parser,
        ITeiEmbeddingService embeddingService,
        ISqlDocumentRepository repository,
        IOptions<IngestionOptions> options,
        ILogger<DocumentIngestionService> logger)
    {
        _parser = parser;
        _embeddingService = embeddingService;
        _repository = repository;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> ProcessFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Processing file: {FilePath}", filePath);
        var fileInfo = new FileInfo(filePath);
        
        var papersDir = _options.PapersDirectory;
        if (!Path.IsPathRooted(papersDir)) papersDir = Path.Combine(Directory.GetCurrentDirectory(), papersDir);
        var relativePath = Path.GetRelativePath(papersDir, filePath);
        
        var isTabular = filePath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || 
                        filePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

        int globalChunkIndex = 0;
        int docCount = 0;

        // Drop DiskANN vector index once before all DML operations.
        // SQL Server blocks INSERT/UPDATE/DELETE while this index exists.
        await _repository.DropVectorIndexAsync(cancellationToken);

        try
        {
            await foreach (var doc in _parser.ParseStreamAsync(filePath).WithCancellation(cancellationToken))
            {
                docCount++;

                List<IngestedDocument> chunks;
                if (isTabular)
                {
                    chunks = new List<IngestedDocument> { doc };
                }
                else
                {
                    chunks = TextSplitter.SplitDocuments(new List<IngestedDocument> { doc }, _options.ChunkSize, _options.ChunkOverlap);
                }

                if (chunks.Count == 0) continue;

                // Process chunks in batches
                var batchSize = _options.BatchSize;
                for (int i = 0; i < chunks.Count; i += batchSize)
                {
                    var batch = chunks.Skip(i).Take(batchSize).ToList();
                    var texts = batch.Select(c => c.PageContent).ToList();
                    
                    try
                    {
                        var embeddings = await _embeddingService.EmbedTextsAsync(texts, cancellationToken);
                        
                        var ids = new List<string>();
                        var metadatas = new List<JsonElement>();

                        for (int j = 0; j < batch.Count; j++)
                        {
                            var chunk = batch[j];
                            var id = TextSplitter.GenerateChunkId(relativePath, fileInfo.LastWriteTimeUtc, fileInfo.Length, globalChunkIndex);
                            
                            chunk.Metadata["relative_path"] = relativePath;
                            chunk.Metadata["chunk_index"] = globalChunkIndex;

                            ids.Add(id);
                            metadatas.Add(JsonSerializer.SerializeToElement(chunk.Metadata));
                            globalChunkIndex++;
                        }

                        var request = new AddDocumentsRequest
                        {
                            Documents = texts,
                            Ids = ids,
                            Metadatas = metadatas
                        };

                        await _repository.UpsertDocumentsAsync(request, embeddings, cancellationToken);
                        _logger.LogInformation("Saved {Count} chunks to DB for {FilePath} (total chunks so far: {Total})", batch.Count, filePath, globalChunkIndex);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error ingesting chunks for {FilePath}", filePath);
                        return false;
                    }
                }
            }

            if (docCount == 0)
            {
                _logger.LogWarning("No content extracted from {FilePath}", filePath);
            }
            else
            {
                _logger.LogInformation("Completed ingestion for {FilePath}: {TotalChunks} total chunks", filePath, globalChunkIndex);
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing file {FilePath}", filePath);
            return false;
        }
        finally
        {
            // Always attempt to recreate the vector index even if ingestion is canceled or fails.
            await _repository.RecreateVectorIndexAsync(CancellationToken.None);
        }
    }
}
