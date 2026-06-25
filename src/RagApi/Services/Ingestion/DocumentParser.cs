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
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        var fileName = Path.GetFileName(filePath);
        
        var parser = _parsers.FirstOrDefault(p => p.CanParse(extension));
        if (parser != null)
        {
            try
            {
                return await parser.ParseAsync(filePath, fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse document {FileName}", fileName);
                return new List<IngestedDocument>();
            }
        }

        _logger.LogWarning("No parser found for extension {Extension}", extension);
        return new List<IngestedDocument>();
    }
}
