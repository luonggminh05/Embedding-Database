using System.Globalization;
using System.Text;

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
        "lam sao", "lam the nao", "cach", "cac buoc", "buoc",
        "thuc hien", "huong dan", "tao", "them", "sua", "xoa",
        "duyet", "tim kiem", "nhu the nao", "lam gi",
        "thao tac", "quy trinh", "trinh tu"
    ];

    private static readonly string[] UiKeywords =
    [
        "o dau", "nam o", "nam dau", "cho nao",
        "nut", "truong", "man hinh", "giao dien",
        "tab", "menu", "sidebar", "toolbar",
        "ben trai", "ben phai", "phia tren", "phia duoi",
        "co nhung gi", "gom nhung gi", "hien thi gi",
        "cot nao", "truong nao", "o nao",
        "vi tri", "layout", "panel"
    ];

    public static QueryIntent Classify(string message)
    {
        var normalized = NormalizeVietnamese(message);
        var hasTask = ContainsAny(normalized, TaskKeywords);
        var hasUi = ContainsAny(normalized, UiKeywords);

        if (hasTask && !hasUi) return QueryIntent.TaskInstruction;
        if (hasUi && !hasTask) return QueryIntent.UiLocation;
        // Both or neither → General; context reordering will use default order
        return QueryIntent.General;
    }

    private static bool ContainsAny(string text, string[] values) =>
        values.Any(v => text.Contains(v, StringComparison.OrdinalIgnoreCase));

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
