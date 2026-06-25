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
        
        var docs = await _parser.ParseAsync(filePath);
        if (docs.Count == 0)
        {
            _logger.LogWarning("No content extracted from {FilePath}", filePath);
            return true; // Consider true as it doesn't need to be retried
        }

        var isTabular = filePath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || 
                        filePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

        List<IngestedDocument> chunks;
        if (isTabular)
        {
            chunks = docs; // Tabular files are already chunked by row
        }
        else
        {
            chunks = TextSplitter.SplitDocuments(docs, _options.ChunkSize, _options.ChunkOverlap);
        }

        if (chunks.Count == 0)
        {
            _logger.LogWarning("No chunks created from {FilePath}", filePath);
            return true; // Nothing to ingest, don't retry
        }

        _logger.LogInformation("Created {ChunkCount} chunks from {FilePath}", chunks.Count, filePath);

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
                    var globalIndex = i + j;
                    var id = TextSplitter.GenerateChunkId(relativePath, fileInfo.LastWriteTimeUtc, fileInfo.Length, globalIndex);
                    
                    chunk.Metadata["relative_path"] = relativePath;
                    chunk.Metadata["chunk_index"] = globalIndex;

                    ids.Add(id);
                    metadatas.Add(JsonSerializer.SerializeToElement(chunk.Metadata));
                }

                var request = new AddDocumentsRequest
                {
                    Documents = texts,
                    Ids = ids,
                    Metadatas = metadatas
                };

                await _repository.UpsertDocumentsAsync(request, embeddings, cancellationToken);
                _logger.LogInformation("Successfully ingested batch {BatchNumber} ({Count} chunks) for {FilePath}", (i / batchSize) + 1, batch.Count, filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error ingesting batch {BatchNumber} for {FilePath}", (i / batchSize) + 1, filePath);
                return false; // Fail early and return false to trigger retry later
            }
        }

        return true;
    }
}
