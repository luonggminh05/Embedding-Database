using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RagApi.Services.Ingestion;

internal static class VisionCaptionQuality
{
    private static readonly string[] GenericPhrases =
    [
        "this image shows",
        "this image contains",
        "this is a web page",
        "software interface",
        "web browser",
        "rag search system",
        "below is a description",
        "duoi day la mo ta",
        "hinh anh nay cho thay",
        "giao dien phan mem",
        "trinh duyet web",
        "cong cu ocr"
    ];

    private static readonly string[] PlaceholderEchoes =
    [
        "<visible title",
        "<visible text",
        "<verbatim important",
        "<table names",
        "<button names",
        "<only if useful",
        "<ghi ",
        "<chep ",
        "<liet ke ",
        "<chi ghi "
    ];


    public static bool IsUseful(string? caption, string? ocrText)
    {
        if (string.IsNullOrWhiteSpace(caption))
        {
            return false;
        }

        var normalized = Normalize(caption);
        if (normalized.Length < 40)
        {
            return false;
        }

        if (caption.Count(ch => ch == '?') >= 5)
        {
            return false;
        }

        if (PlaceholderEchoes.Any(normalized.Contains))
        {
            return false;
        }

        if (HasRepeatedLines(caption) || HasDegenerateRepetition(caption) || HasNumericRunaway(caption))
        {
            return false;
        }

        var hasStructuredEvidence = ContainsAny(normalized,
            "image_type", "system_or_app_name", "screen_or_document_title", "visible_text", "ui_panels", "search_conditions", "data_tables", "email_info", "buttons_or_actions", "counts", "question_answer_hints", "searchable_summary",
            "dieu kien", "tim kiem", "to trinh", "so to", "trang thai", "ngay", "tu ngay", "den ngay",
            "nut", "bang", "cot", "dong", "o nhap", "checkbox", "combobox", "button", "field", "table", "panel", "textbox", "dropdown", "date-picker", "email", "from", "subject", "attachment");

        if (!hasStructuredEvidence && GenericPhrases.Any(normalized.Contains))
        {
            return false;
        }

        if (!hasStructuredEvidence)
        {
            return false;
        }

        return CountLetters(caption) >= 20;
    }


    private static bool HasDegenerateRepetition(string text)
    {
        var normalized = Normalize(text);
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 20)
        {
            return false;
        }

        var mostCommonTokenCount = tokens
            .GroupBy(token => token)
            .Max(group => group.Count());

        return mostCommonTokenCount >= 12 || mostCommonTokenCount >= tokens.Length * 0.45;
    }

    private static bool HasNumericRunaway(string text)
    {
        var compact = Regex.Replace(text, @"\s+", string.Empty);
        if (compact.Length < 120)
        {
            return false;
        }

        var digitCount = compact.Count(char.IsDigit);
        var letterCount = compact.Count(char.IsLetter);
        if (digitCount > letterCount * 3 && Regex.IsMatch(compact, @"(?:\d[\d.,]*){80,}"))
        {
            return true;
        }

        return Regex.IsMatch(compact, @"(?:0{8,}|0001|0002|\.000){8,}");
    }
    private static bool HasRepeatedLines(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(line => line.Length > 12)
            .ToArray();

        if (lines.Length < 4)
        {
            return false;
        }

        return lines.GroupBy(line => line).Any(group => group.Count() >= 3);
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static int CountLetters(string text) => text.Count(char.IsLetter);

    private static string Normalize(string text)
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

        return Regex.Replace(builder.ToString().Normalize(NormalizationForm.FormC), "\\s+", " ");
    }
}
