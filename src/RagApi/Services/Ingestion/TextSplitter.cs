using System.Security.Cryptography;
using System.Text;

namespace RagApi.Services.Ingestion;

public class IngestedDocument
{
    public required string PageContent { get; set; }
    public required Dictionary<string, object> Metadata { get; set; }
}

public static class TextSplitter
{
    public static List<IngestedDocument> SplitDocuments(List<IngestedDocument> documents, int chunkSize, int chunkOverlap)
    {
        var result = new List<IngestedDocument>();

        foreach (var doc in documents)
        {
            var text = doc.PageContent;
            if (string.IsNullOrWhiteSpace(text)) continue;

            if (text.Length <= chunkSize)
            {
                result.Add(doc);
                continue;
            }

            int currentIdx = 0;
            while (currentIdx < text.Length)
            {
                int length = Math.Min(chunkSize, text.Length - currentIdx);
                
                // Try to find a space to break on if we are not at the end
                if (currentIdx + length < text.Length)
                {
                    int lastSpace = text.LastIndexOf(' ', currentIdx + length - 1, chunkOverlap);
                    if (lastSpace != -1 && lastSpace > currentIdx)
                    {
                        length = lastSpace - currentIdx;
                    }
                }

                var chunkText = text.Substring(currentIdx, length).Trim();
                if (!string.IsNullOrWhiteSpace(chunkText))
                {
                    // Copy metadata
                    var newMetadata = new Dictionary<string, object>(doc.Metadata);
                    result.Add(new IngestedDocument
                    {
                        PageContent = chunkText,
                        Metadata = newMetadata
                    });
                }

                if (currentIdx + length >= text.Length) break;
                
                // Ensure we always advance forward
                int advance = length - chunkOverlap;
                if (advance <= 0) advance = 1;
                
                currentIdx += advance;
            }
        }

        return result;
    }

    public static string GenerateChunkId(string relativePath, DateTime lastWriteTimeUtc, long length, int index)
    {
        var input = $"{relativePath}|{lastWriteTimeUtc.Ticks}|{length}";
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = sha256.ComputeHash(bytes);
        var hashString = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        return $"{hashString}_chunk_{index}";
    }
}
