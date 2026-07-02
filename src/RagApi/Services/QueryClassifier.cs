using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RagApi.Services;

public enum QueryIntent
{
    /// <summary>User asks how to perform an action (e.g., "làm thế nào", "các bước", "cách tạo").</summary>
    TaskInstruction,
    /// <summary>User asks about UI location/layout (e.g., "nút ở đâu", "màn hình gồm gì").</summary>
    UiLocation,
    /// <summary>General question without clear intent towards instruction or UI.</summary>
    General
}

public static class QueryClassifier
{
    private static readonly string[] TaskKeywords =
    [
        "cach", "cac buoc", "buoc", "huong dan", "quy trinh", "trinh tu", "thao tac", "lam sao", "lam the nao", "nhu the nao",
        "thuc hien", "tao", "them", "sua", "xoa", "duyet", "tim kiem", "lam gi", "lam nhung gi", "can lam gi", "phai lam gi"
    ];

    private static readonly string[] UiKeywords =
    [
        "o dau", "nam o", "nam dau", "cho nao", "cot nao", "o nao", "vi tri", "co nhung gi", "gom nhung gi", "hien thi gi", "layout",
        "nut", "truong", "man hinh", "giao dien", "tab", "menu", "sidebar", "toolbar", "ben trai", "ben phai", "phia tren", "phia duoi", "truong nao", "panel"
    ];

    private static readonly (string Keyword, string ExcludedPhrase)[] Exclusions =
    [
        ("tao", "dao tao"),
        ("buoc", "bat buoc")
    ];

    public static QueryIntent Classify(string message)
    {
        var normalized = NormalizeVietnamese(message);
        
        var hasTask = ContainsAny(normalized, TaskKeywords);
        var hasUi = ContainsAny(normalized, UiKeywords);

        if (hasTask && !hasUi)
        {
            return QueryIntent.TaskInstruction;
        }

        if (hasUi && !hasTask)
        {
            return QueryIntent.UiLocation;
        }

        return QueryIntent.General;
    }

    private static bool ContainsAny(string text, string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (MatchKeywordWithExclusions(text, keyword))
            {
                return true;
            }
        }
        return false;
    }

    private static bool MatchKeywordWithExclusions(string text, string keyword)
    {
        // Boundary-aware regex match for the keyword
        var keywordPattern = $"(?<![\\p{{L}}\\p{{N}}]){Regex.Escape(keyword)}(?![\\p{{L}}\\p{{N}}])";
        var matches = Regex.Matches(text, keywordPattern, RegexOptions.IgnoreCase);
        if (matches.Count == 0)
        {
            return false;
        }

        // Exclusions configured for this keyword
        var exclusionsForKeyword = Exclusions.Where(e => e.Keyword == keyword).Select(e => e.ExcludedPhrase).ToList();
        if (exclusionsForKeyword.Count == 0)
        {
            return true;
        }

        var excludedSpans = new List<(int Start, int End)>();
        foreach (var phrase in exclusionsForKeyword)
        {
            var phrasePattern = $"(?<![\\p{{L}}\\p{{N}}]){Regex.Escape(phrase)}(?![\\p{{L}}\\p{{N}}])";
            var phraseMatches = Regex.Matches(text, phrasePattern, RegexOptions.IgnoreCase);
            foreach (Match pm in phraseMatches)
            {
                excludedSpans.Add((pm.Index, pm.Index + pm.Length));
            }
        }

        // At least one keyword match must not fall within any excluded phrase matches
        foreach (Match m in matches)
        {
            bool isExcluded = false;
            foreach (var span in excludedSpans)
            {
                if (m.Index >= span.Start && (m.Index + m.Length) <= span.End)
                {
                    isExcluded = true;
                    break;
                }
            }

            if (!isExcluded)
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeVietnamese(string text)
    {
        var normalized = text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);

        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(ch == '\u0111' ? 'd' : ch);
            }
        }

        return string.Join(' ', builder.ToString().Normalize(NormalizationForm.FormC)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
