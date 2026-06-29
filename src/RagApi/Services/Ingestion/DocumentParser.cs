using RagApi.Services.Ingestion.Parsers;

namespace RagApi.Services.Ingestion;

public class DocumentParser
{
    private readonly IEnumerable<IDocumentParser> _parsers;
    private readonly ILogger<DocumentParser> _logger;

    public DocumentParser(IEnumerable<IDocumentParser> parsers, ILogger<DocumentParser> logger)
    {
        _parsers = parsers;
        _logger = logger;
    }

    public async Task<List<IngestedDocument>> ParseAsync(string filePath)
    {
        var docs = new List<IngestedDocument>();
        await foreach (var doc in ParseStreamAsync(filePath))
        {
            docs.Add(doc);
        }
        return docs;
    }

    public async IAsyncEnumerable<IngestedDocument> ParseStreamAsync(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        var fileName = Path.GetFileName(filePath);
        
        var parser = _parsers.FirstOrDefault(p => p.CanParse(extension));
        if (parser == null)
        {
            _logger.LogWarning("No parser found for extension {Extension}", extension);
            yield break;
        }

        IAsyncEnumerable<IngestedDocument> stream;
        try
        {
            stream = parser.ParseStreamAsync(filePath, fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start parsing document {FileName}", fileName);
            throw;
        }

        await using var enumerator = stream.GetAsyncEnumerator();
        while (true)
        {
            IngestedDocument current;
            try
            {
                if (!await enumerator.MoveNextAsync()) yield break;
                current = enumerator.Current;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed while parsing document {FileName}", fileName);
                throw;
            }
            yield return current;
        }
    }
}
