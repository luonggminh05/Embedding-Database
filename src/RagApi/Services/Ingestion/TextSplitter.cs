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
        // Normal ingestion path must be validated by IngestionOptions.Validate() first.
        // This is a secondary defense line for direct API callers/tests.
        if (chunkSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "chunkSize must be > 0");
        }
        if (chunkOverlap < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkOverlap), "chunkOverlap must be >= 0");
        }
        if (chunkOverlap > chunkSize / 2)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkOverlap), "chunkOverlap must be <= chunkSize / 2");
        }

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
                int targetLength = Math.Min(chunkSize, text.Length - currentIdx);
                int actualLength = targetLength;
                
                int baselineAdvance = chunkSize - chunkOverlap;
                int minAdvance = Math.Max(baselineAdvance / 4, 1);
                
                // Try to find a space to break on if we are not at the end
                if (currentIdx + targetLength < text.Length)
                {
                    int lastSpace = text.LastIndexOf(' ', currentIdx + targetLength - 1, chunkOverlap);
                    if (lastSpace != -1 && lastSpace > currentIdx)
                    {
                        int candidateLength = lastSpace - currentIdx;
                        // Avoid using space cuts too early to prevent data loss when currentIdx advances.
                        if (candidateLength >= chunkOverlap + minAdvance)
                        {
                            actualLength = candidateLength;
                        }
                    }
                }

                var chunkText = text.Substring(currentIdx, actualLength).Trim();
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

                if (currentIdx + actualLength >= text.Length) break;
                
                int advance = Math.Max(actualLength - chunkOverlap, minAdvance);
                
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
