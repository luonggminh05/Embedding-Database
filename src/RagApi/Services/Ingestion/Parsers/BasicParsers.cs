using System.Text;


namespace RagApi.Services.Ingestion.Parsers;

public interface IDocumentParser
{
    bool CanParse(string extension);
    Task<List<IngestedDocument>> ParseAsync(string filePath, string fileName);

    /// <summary>
    /// </summary>
    async IAsyncEnumerable<IngestedDocument> ParseStreamAsync(string filePath, string fileName)
    {
        var docs = await ParseAsync(filePath, fileName);
        foreach (var doc in docs)
        {
            yield return doc;
        }
    }
}



public class TextParser : IDocumentParser
{
    public bool CanParse(string extension) => extension == ".txt" || extension == ".md";

    public async Task<List<IngestedDocument>> ParseAsync(string filePath, string fileName)
    {
        var text = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
        return new List<IngestedDocument>
        {
            new IngestedDocument
            {
                PageContent = text,
                Metadata = new Dictionary<string, object> { { "source", fileName } }
            }
        };
    }
}
