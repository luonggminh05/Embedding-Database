using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace RagApi.Services.Ingestion;

public static class NearDuplicateDetector
{
    public static void DetectAndLog(List<IngestedDocument> documents, string fileName, ILogger logger)
    {
        var instructionChunks = documents
            .Where(d => d.Metadata.TryGetValue("content_kind", out var ck) && "instruction_text".Equals(ck))
            .ToList();

        for (int i = 0; i < instructionChunks.Count; i++)
        {
            for (int j = i + 1; j < instructionChunks.Count; j++)
            {
                var docA = instructionChunks[i];
                var docB = instructionChunks[j];

                var linesA = GetNormalizedLines(docA.PageContent);
                var linesB = GetNormalizedLines(docB.PageContent);

                if (linesA.Count < 4 || linesB.Count < 4) continue;

                int intersection = linesA.Intersect(linesB).Count();
                double overlapRatio = (double)intersection / Math.Max(linesA.Count, linesB.Count);

                if (overlapRatio >= 0.8)
                {
                    docA.Metadata.TryGetValue("slide_title", out var titleAObj);
                    docB.Metadata.TryGetValue("slide_title", out var titleBObj);
                    docA.Metadata.TryGetValue("action_kind", out var actionAObj);
                    docB.Metadata.TryGetValue("action_kind", out var actionBObj);

                    var slideA = titleAObj?.ToString();
                    var slideB = titleBObj?.ToString();
                    var actionA = actionAObj?.ToString();
                    var actionB = actionBObj?.ToString();

                    if (slideA != slideB || actionA != actionB)
                    {
                        logger.LogWarning(
                            "Near-duplicate slides detected in {FileName} with different metadata. Slide A: '{SlideA}' ({ActionA}), Slide B: '{SlideB}' ({ActionB}). Overlap: {OverlapRatio:P1}",
                            fileName, slideA, actionA, slideB, actionB, overlapRatio);
                    }
                }
            }
        }
    }

    private static HashSet<string> GetNormalizedLines(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return new HashSet<string>();
        var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            // Skip section headers like [SLIDE: ...] or [VĂN BẢN HƯỚNG DẪN] to get pure content comparison
            if (trimmed.StartsWith("[SLIDE:") || trimmed.StartsWith("[V\u0102N B\u1EA2N")) continue;
            
            if (!string.IsNullOrEmpty(trimmed))
            {
                set.Add(NormalizeText(trimmed));
            }
        }
        return set;
    }

    private static string NormalizeText(string text)
    {
        var normalized = text.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var builder = new System.Text.StringBuilder(normalized.Length);

        foreach (var ch in normalized)
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                builder.Append(ch == '\u0111' ? 'd' : ch);
            }
        }

        return string.Join(' ', builder.ToString().Normalize(System.Text.NormalizationForm.FormC)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
