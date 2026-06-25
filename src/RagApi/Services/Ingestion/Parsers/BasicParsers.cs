using System.Text;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;

namespace RagApi.Services.Ingestion.Parsers;

public interface IDocumentParser
{
    bool CanParse(string extension);
    Task<List<IngestedDocument>> ParseAsync(string filePath, string fileName);
}

public class PdfParser : IDocumentParser
{
    public bool CanParse(string extension) => extension == ".pdf";

    public Task<List<IngestedDocument>> ParseAsync(string filePath, string fileName)
    {
        var docs = new List<IngestedDocument>();
        using var document = PdfDocument.Open(filePath);
        foreach (var page in document.GetPages())
        {
            var text = page.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                docs.Add(new IngestedDocument
                {
                    PageContent = text,
                    Metadata = new Dictionary<string, object>
                    {
                        { "source", fileName },
                        { "page", page.Number }
                    }
                });
            }
        }
        return Task.FromResult(docs);
    }
}

public class WordParser : IDocumentParser
{
    public bool CanParse(string extension) => extension == ".docx";

    public Task<List<IngestedDocument>> ParseAsync(string filePath, string fileName)
    {
        var docs = new List<IngestedDocument>();
        using var document = WordprocessingDocument.Open(filePath, false);
        var body = document.MainDocumentPart?.Document?.Body;
        if (body != null)
        {
            docs.Add(new IngestedDocument
            {
                PageContent = body.InnerText,
                Metadata = new Dictionary<string, object> { { "source", fileName } }
            });
        }
        return Task.FromResult(docs);
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
