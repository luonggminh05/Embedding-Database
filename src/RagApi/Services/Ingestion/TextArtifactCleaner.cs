using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RagApi.Services.Ingestion;

public static class TextArtifactCleaner
{
    public static string Clean(string text, Dictionary<string, int>? lineOccurrences = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var cleanedLines = new List<string>();
        var localOccurrences = lineOccurrences ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            // Xóa artifact rõ ràng như \begin, \end, \[, \]
            if (trimmed.Contains("\\begin") || trimmed.Contains("\\end") || trimmed.Contains("\\[") || trimmed.Contains("\\]"))
            {
                continue;
            }

            // Dòng chỉ gồm ký hiệu bảng/toán (không có chữ cái hay chữ số nào)
            if (!trimmed.Any(char.IsLetterOrDigit))
            {
                continue;
            }

            // Dedup theo toàn bộ dòng sau NormalizeText
            var normalized = NormalizeText(trimmed);
            localOccurrences.TryGetValue(normalized, out int count);
            if (count >= 2)
            {
                continue;
            }

            localOccurrences[normalized] = count + 1;
            cleanedLines.Add(trimmed);
        }

        return string.Join("\n", cleanedLines);
    }

    public static string NormalizeText(string text)
    {
        var normalized = text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);

        foreach (var character in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character == '\u0111' ? 'd' : character);
            }
        }

        return string.Join(' ', builder.ToString().Normalize(NormalizationForm.FormC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
