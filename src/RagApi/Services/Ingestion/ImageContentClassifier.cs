namespace RagApi.Services.Ingestion;

/// <summary>Classification of an image's role in a document.</summary>
public enum ImageRole
{
    /// <summary>Small inline icon/button with short text label.</summary>
    InlineIcon,
    /// <summary>Large screenshot or UI image needing Vision caption.</summary>
    UiScreenshot,
    /// <summary>Cannot determine; treat conservatively as screenshot.</summary>
    UnknownImage
}

/// <summary>
/// Shared helper to classify images extracted from any document format
/// (PPT, Word, PDF) into inline icons vs UI screenshots.
/// </summary>
public static class ImageContentClassifier
{
    /// <summary>Max OCR text length to treat as an inline button/icon label.</summary>
    public const int InlineOcrMaxLength = 50;

    /// <summary>
    /// Classifies an image based on its dimensions and OCR text.
    /// </summary>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="ocrText">OCR-extracted text from the image (may be null/empty).</param>
    /// <param name="minVisionWidth">Minimum width for Vision-eligible images.</param>
    /// <param name="minVisionHeight">Minimum height for Vision-eligible images.</param>
    /// <returns>The classified <see cref="ImageRole"/>.</returns>
    public static ImageRole Classify(
        int width, int height,
        string? ocrText,
        int minVisionWidth, int minVisionHeight)
    {
        var isSmallImage = width < minVisionWidth || height < minVisionHeight;
        var hasOcr = !string.IsNullOrWhiteSpace(ocrText);
        var isShortOcr = hasOcr && ocrText!.Length <= InlineOcrMaxLength;

        // Small image with readable short text is treated as an inline icon/button.
        if (isSmallImage && isShortOcr)
            return ImageRole.InlineIcon;

        // Small image with no text may be an icon-only button; keep it inline.
        if (isSmallImage && !hasOcr)
            return ImageRole.InlineIcon;
        // Large images are treated as screenshots even when OCR is short or empty.
        if (!isSmallImage)
            return ImageRole.UiScreenshot;

        // Small image with long OCR is unusual; fall back conservatively.
        return ImageRole.UnknownImage;
    }

    /// <summary>
    /// Builds an inline marker string for a small image/icon/button.
    /// </summary>
    public static string BuildInlineMarker(string? ocrText)
    {
        if (!string.IsNullOrWhiteSpace(ocrText))
            return $"[N\u00FAt/\u1EA2nh: {ocrText.Trim()}]";
        return "[\u1EA2nh nh\u1ECF]";
    }

    /// <summary>
    /// Builds a short placeholder marker for a large image in instruction text.
    /// </summary>
    public static string BuildScreenshotPlaceholder() => "[\u1EA2nh giao di\u1EC7n]";
}

