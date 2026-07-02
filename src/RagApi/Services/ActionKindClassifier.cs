using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RagApi.Services;

public static class ActionKindClassifier
{
    public const string TimKiem = "tim_kiem";
    public const string ThemMoi = "them_moi";
    public const string LuuNhap = "luu_nhap";
    public const string GuiPheDuyet = "gui_phe_duyet";
    public const string Duyet = "duyet";
    public const string TuChoi = "tu_choi";
    public const string UyQuyen = "uy_quyen";
    public const string BanGiao = "ban_giao";
    public const string ChiaSe = "chia_se";
    public const string In = "in";
    public const string ThemGhiChu = "them_ghi_chu";
    public const string DinhKem = "dinh_kem";

    private sealed record ActionPattern(string Kind, string Phrase);

    private static readonly ActionPattern[] Patterns =
    [
        new(GuiPheDuyet, "gui phe duyet"),
        new(ThemGhiChu, "them ghi chu"),
        new(TimKiem, "tim kiem"),
        new(ThemMoi, "them moi"),
        new(LuuNhap, "luu nhap"),
        new(TuChoi, "tu choi"),
        new(UyQuyen, "uy quyen"),
        new(BanGiao, "ban giao"),
        new(ChiaSe, "chia se"),
        new(Duyet, "phe duyet"),
        new(ThemMoi, "tao moi"),
        new(ThemMoi, "them"),
        new(Duyet, "duyet"),
        new(In, "in"),
        new(DinhKem, "dinh kem hinh anh"),
        new(DinhKem, "dinh kem anh"),
        new(DinhKem, "dinh kem file"),
        new(DinhKem, "dinh kem"),
        new(TimKiem, "xem chi tiet")
    ];

    private static readonly ActionPattern[] OrderedPatterns = Patterns
        .OrderByDescending(pattern => pattern.Phrase.Length)
        .ToArray();

    public static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

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

        return Regex.Replace(builder.ToString().Normalize(NormalizationForm.FormC), @"\s+", " ");
    }

    public static string? DetermineActionKind(string? text)
    {
        var normalized = NormalizeText(text);
        if (normalized.Length == 0)
        {
            return null;
        }

        foreach (var pattern in OrderedPatterns)
        {
            if (ContainsPhrase(normalized, pattern.Phrase))
            {
                return pattern.Kind;
            }
        }

        return null;
    }

    public static bool HasActionIntent(string? text) => DetermineActionKind(text) != null;

    public static string? NormalizeActionKind(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var normalized = raw.Trim().ToLowerInvariant();
        return normalized switch
        {
            "phe_duyet" => Duyet,
            TimKiem or ThemMoi or LuuNhap or GuiPheDuyet or Duyet or TuChoi or UyQuyen or BanGiao or ChiaSe or In or ThemGhiChu or DinhKem => normalized,
            _ => normalized
        };
    }

    public static string? GetActionKeyword(string? actionKind)
    {
        return NormalizeActionKind(actionKind) switch
        {
            TimKiem => "tim kiem",
            ThemMoi => "them moi",
            LuuNhap => "luu nhap",
            GuiPheDuyet => "gui phe duyet",
            Duyet => "duyet",
            TuChoi => "tu choi",
            UyQuyen => "uy quyen",
            BanGiao => "ban giao",
            ChiaSe => "chia se",
            In => "in",
            ThemGhiChu => "them ghi chu",
            DinhKem => "dinh kem",
            _ => null
        };
    }

    public static IReadOnlyList<string> AllActionKinds { get; } =
    [
        TimKiem,
        ThemMoi,
        LuuNhap,
        GuiPheDuyet,
        Duyet,
        TuChoi,
        UyQuyen,
        BanGiao,
        ChiaSe,
        In,
        ThemGhiChu,
        DinhKem
    ];

    private static bool ContainsPhrase(string normalizedText, string normalizedPhrase)
    {
        if (normalizedPhrase == In)
        {
            return Regex.IsMatch(normalizedText, @"\bin\b");
        }

        return Regex.IsMatch(normalizedText, $@"(^|\s){Regex.Escape(normalizedPhrase)}(\s|$)");
    }
}
