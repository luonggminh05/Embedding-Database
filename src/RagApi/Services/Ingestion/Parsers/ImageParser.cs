using RagApi.Services.Ingestion;

namespace RagApi.Services.Ingestion.Parsers;

public class ImageParser : IDocumentParser
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff"
    };

    private readonly IOcrService _ocrService;
    private readonly IVisionCaptionService _visionCaptionService;
    private readonly ILogger<ImageParser> _logger;

    public ImageParser(
        IOcrService ocrService,
        IVisionCaptionService visionCaptionService,
        ILogger<ImageParser> logger)
    {
        _ocrService = ocrService;
        _visionCaptionService = visionCaptionService;
        _logger = logger;
    }

    public bool CanParse(string extension) => SupportedExtensions.Contains(extension);

    public async Task<List<IngestedDocument>> ParseAsync(string filePath, string fileName)
    {
        var docs = new List<IngestedDocument>();
        byte[] imageBytes;

        try
        {
            imageBytes = await File.ReadAllBytesAsync(filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read image {FileName}", fileName);
            return docs;
        }

        if (!ImageUtilities.TryGetDimensions(imageBytes, out var width, out var height))
        {
            _logger.LogWarning("Skipping image {FileName}: could not decode dimensions.", fileName);
            return docs;
        }

        var metadata = new Dictionary<string, object>
        {
            { "source", fileName },
            { "type", "image" },
            { "width", width },
            { "height", height }
        };

        var ocrText = await _ocrService.ExtractTextAsync(imageBytes);
        var caption = await _visionCaptionService.GenerateCaptionAsync(imageBytes, metadata, ocrText);
        var usefulCaption = VisionCaptionQuality.IsUseful(caption, ocrText) ? caption : null;

        if (string.IsNullOrWhiteSpace(usefulCaption) && string.IsNullOrWhiteSpace(ocrText))
        {
            return docs;
        }

        var contentParts = new List<string>
        {
            $"Image dimensions: {width}x{height}px."
        };

        if (!string.IsNullOrWhiteSpace(ocrText))
        {
            contentParts.Add($"OCR text: {ocrText}");
        }

        if (!string.IsNullOrWhiteSpace(usefulCaption))
        {
            contentParts.Add($"Vision caption: {usefulCaption}");
        }

        metadata["has_ocr_text"] = !string.IsNullOrWhiteSpace(ocrText);
        metadata["caption_source"] = !string.IsNullOrWhiteSpace(usefulCaption) ? "ocr+vision" : "ocr";

        docs.Add(new IngestedDocument
        {
            PageContent = string.Join("\n", contentParts),
            Metadata = metadata
        });

        return docs;
    }
}
