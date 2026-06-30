using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Options;
using RagApi.Options;
using A = DocumentFormat.OpenXml.Drawing;

namespace RagApi.Services.Ingestion.Parsers;

public class WordParser : IDocumentParser
{
    private readonly IOcrService _ocrService;
    private readonly IVisionCaptionService _visionCaptionService;
    private readonly IngestionOptions _options;
    private readonly ILogger<WordParser> _logger;

    public WordParser(
        IOcrService ocrService,
        IVisionCaptionService visionCaptionService,
        IOptions<IngestionOptions> options,
        ILogger<WordParser> logger)
    {
        _ocrService = ocrService;
        _visionCaptionService = visionCaptionService;
        _options = options.Value;
        _logger = logger;
    }

    public bool CanParse(string extension) => extension == ".docx";

    public async Task<List<IngestedDocument>> ParseAsync(string filePath, string fileName)
    {
        var docs = new List<IngestedDocument>();
        await foreach (var doc in ParseStreamAsync(filePath, fileName))
        {
            docs.Add(doc);
        }
        return docs;
    }

    public async IAsyncEnumerable<IngestedDocument> ParseStreamAsync(string filePath, string fileName)
    {
        using var wordDoc = WordprocessingDocument.Open(filePath, false);
        var mainPart = wordDoc.MainDocumentPart;
        var body = mainPart?.Document?.Body;

        if (body == null)
        {
            yield break;
        }

        var imageChunks = new List<IngestedDocument>();
        var paragraphIndex = 0;
        var imageCounter = new int[] { 0 };

        foreach (var paragraph in body.Elements<Paragraph>())
        {
            paragraphIndex++;
            var paragraphText = new System.Text.StringBuilder();

            foreach (var run in paragraph.Elements<Run>())
            {
                if (run.Elements<Text>().Any())
                {
                    paragraphText.Append(run.InnerText);
                }

                foreach (var drawing in run.Elements<Drawing>())
                {
                    await ProcessDrawingAsync(
                        drawing, mainPart!, paragraphText, imageChunks,
                        fileName, paragraphIndex, imageCounter);
                }
            }

            var lineText = paragraphText.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(lineText))
            {
                yield return new IngestedDocument
                {
                    PageContent = $"[V\u0102N B\u1ea2N H\u01af\u1edaNG D\u1eaaN]\n{lineText}",
                    Metadata = new Dictionary<string, object>
                    {
                        { "source", fileName },
                        { "type", "docx_text" },
                        { "content_kind", "instruction_text" },
                        { "paragraph_index", paragraphIndex }
                    }
                };
            }
        }

        // Emit each image_ui_description chunk separately
        foreach (var imageChunk in imageChunks)
        {
            yield return imageChunk;
        }
    }

    private async Task ProcessDrawingAsync(
        Drawing drawing,
        MainDocumentPart mainPart,
        System.Text.StringBuilder paragraphText,
        List<IngestedDocument> imageChunks,
        string fileName,
        int paragraphIndex,
        int[] imageCounter)
    {
        // Try to find a Blip (image reference) inside the drawing
        var blip = drawing.Descendants<A.Blip>().FirstOrDefault();
        if (blip?.Embed?.Value == null)
        {
            return;
        }

        var relationshipId = blip.Embed.Value;
        if (mainPart.GetPartById(relationshipId) is not ImagePart imagePart)
        {
            return;
        }

        byte[] imageBytes;
        await using (var stream = imagePart.GetStream(FileMode.Open, FileAccess.Read))
        using (var memory = new MemoryStream())
        {
            await stream.CopyToAsync(memory);
            imageBytes = memory.ToArray();
        }

        if (!ImageUtilities.TryGetDimensions(imageBytes, out var width, out var height))
        {
            _logger.LogWarning("Skipping image in {FileName} paragraph {Paragraph}: could not decode dimensions.", fileName, paragraphIndex);
            return;
        }

        imageCounter[0]++;

        var ocrText = await _ocrService.ExtractTextAsync(imageBytes);
        var baseRole = ImageContentClassifier.Classify(width, height, ocrText, _options.MinVisionImageWidth, _options.MinVisionImageHeight);
        
        // Determine if it's an anchor/floating image (treat as large regardless of size)
        var isAnchor = drawing.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.Anchor>().Any();
        var effectiveRole = isAnchor ? ImageRole.UiScreenshot : baseRole;

        // --- Small inline image with short OCR (button/icon): inject inline marker ---
        if (effectiveRole == ImageRole.InlineIcon)
        {
            paragraphText.Append($" {ImageContentClassifier.BuildInlineMarker(ocrText)} ");
            return;
        }

        // --- Large image or anchor image: short placeholder in text + separate chunk ---
        paragraphText.Append($" {ImageContentClassifier.BuildScreenshotPlaceholder()} ");

        var metadata = new Dictionary<string, object>
        {
            { "source", fileName },
            { "type", "docx_image" },
            { "content_kind", "image_ui_description" },
            { "paragraph_index", paragraphIndex },
            { "image_index", imageCounter[0] },
            { "image_role", isAnchor ? "floating" : "inline_large" },
            { "width", width },
            { "height", height }
        };

        string? caption = null;
        var visionAttempted = false;

        if (_options.EnableVisionCaption
            && !string.IsNullOrWhiteSpace(_options.VisionApiUrl)
            && width >= _options.MinVisionImageWidth
            && height >= _options.MinVisionImageHeight)
        {
            visionAttempted = true;
            caption = await _visionCaptionService.GenerateCaptionAsync(imageBytes, metadata, ocrText);
        }

        var usefulCaption = VisionCaptionQuality.IsUseful(caption, ocrText) ? caption : null;
        var descriptionParts = new List<string>
        {
            $"dimensions: {width}x{height}px"
        };

        if (!string.IsNullOrWhiteSpace(ocrText))
        {
            descriptionParts.Add($"OCR text: {ocrText}");
        }

        if (!string.IsNullOrWhiteSpace(usefulCaption))
        {
            descriptionParts.Add($"Vision caption: {usefulCaption}");
        }
        else if (visionAttempted && string.IsNullOrWhiteSpace(ocrText))
        {
            descriptionParts.Add("Vision caption was empty or filtered as unreliable");
        }

        if (descriptionParts.Count > 1 || visionAttempted)
        {
            imageChunks.Add(new IngestedDocument
            {
                PageContent = $"[M\u00D4 T\u1EA2 GIAO DI\u1EC6N T\u1EEA H\u00CCNH \u1EA2NH]\n[H\u00ECnh \u1EA3nh trong t\u00E0i li\u1EC7u: {string.Join("; ", descriptionParts)}]",
                Metadata = metadata
            });
        }
    }
}
