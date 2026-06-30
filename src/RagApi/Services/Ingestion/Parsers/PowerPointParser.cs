using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Drawing;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Shape = DocumentFormat.OpenXml.Presentation.Shape;
using GraphicFrame = DocumentFormat.OpenXml.Presentation.GraphicFrame;
using Picture = DocumentFormat.OpenXml.Presentation.Picture;
using Table = DocumentFormat.OpenXml.Drawing.Table;
using Text = DocumentFormat.OpenXml.Drawing.Text;
using Microsoft.Extensions.Options;
using RagApi.Options;

namespace RagApi.Services.Ingestion.Parsers;

public class PowerPointParser : IDocumentParser
{
    private readonly IOcrService _ocrService;
    private readonly IVisionCaptionService _visionCaptionService;
    private readonly IngestionOptions _options;
    private readonly ILogger<PowerPointParser> _logger;

    private sealed class VisionBudget
    {
        public int FileAttempts { get; set; }
        public int SlideAttempts { get; set; }
    }

    /// <summary>Holds separated content extracted from a single slide.</summary>
    private sealed class SlideContent
    {
        /// <summary>Text lines from shapes, tables, and inline image markers.</summary>
        public List<string> InstructionalTexts { get; } = new();
        /// <summary>Standalone chunks for large images (OCR + Vision caption).</summary>
        public List<IngestedDocument> ImageChunks { get; } = new();
        /// <summary>Running counter for images on the current slide.</summary>
        public int ImageIndex { get; set; }
    }

    public PowerPointParser(
        IOcrService ocrService,
        IVisionCaptionService visionCaptionService,
        IOptions<IngestionOptions> options,
        ILogger<PowerPointParser> logger)
    {
        _ocrService = ocrService;
        _visionCaptionService = visionCaptionService;
        _options = options.Value;
        _logger = logger;
    }

    public bool CanParse(string extension) => extension == ".pptx";

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
        var emittedOpenXmlDocument = false;
        var enumerator = ParseWithOpenXmlStreamAsync(filePath, fileName).GetAsyncEnumerator();

        try
        {
            while (true)
            {
                IngestedDocument? doc = null;
                List<IngestedDocument>? fallbackDocs = null;

                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        yield break;
                    }

                    doc = enumerator.Current;
                }
                catch (Exception ex) when (!emittedOpenXmlDocument && ex is IOException or InvalidDataException or OpenXmlPackageException)
                {
                    _logger.LogWarning(ex, "Open XML PowerPoint parsing failed for {FileName}. Trying text-only ZIP fallback.", fileName);
                    fallbackDocs = ParseTextFromPackageFallback(filePath, fileName);
                }

                if (fallbackDocs != null)
                {
                    foreach (var fallbackDoc in fallbackDocs)
                    {
                        yield return fallbackDoc;
                    }

                    yield break;
                }

                if (doc == null)
                {
                    yield break;
                }

                emittedOpenXmlDocument = true;
                yield return doc;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    private async IAsyncEnumerable<IngestedDocument> ParseWithOpenXmlStreamAsync(string filePath, string fileName)
    {
        using var presentationDocument = PresentationDocument.Open(filePath, false);
        var presentationPart = presentationDocument.PresentationPart;
        var visionBudget = new VisionBudget();
        
        if (presentationPart?.Presentation?.SlideIdList != null)
        {
            int slideIndex = 1;
            foreach (var slideId in presentationPart.Presentation.SlideIdList.Elements<SlideId>())
            {
                if (slideId.RelationshipId == null) continue;
                
                visionBudget.SlideAttempts = 0;
                var slidePart = (SlidePart)presentationPart.GetPartById(slideId.RelationshipId!);
                var slideContent = await ExtractSlideContentAsync(slidePart, fileName, slideIndex, visionBudget);
                
                // Emit instruction_text chunk (text from shapes/tables + inline image markers)
                var instructionText = string.Join("\n", slideContent.InstructionalTexts);
                if (!string.IsNullOrWhiteSpace(instructionText))
                {
                    yield return new IngestedDocument
                    {
                        PageContent = $"[V\u0102N B\u1EA2N H\u01AF\u1EDA\u004E\u0047 D\u1EAAN]\n{instructionText}",
                        Metadata = new Dictionary<string, object>
                        {
                            { "source", fileName },
                            { "slide", slideIndex },
                            { "type", "pptx_slide" },
                            { "content_kind", "instruction_text" }
                        }
                    };
                }

                // Emit each image_ui_description chunk separately
                foreach (var imageChunk in slideContent.ImageChunks)
                {
                    yield return imageChunk;
                }

                slideIndex++;
            }
        }
    }

    private static List<IngestedDocument> ParseTextFromPackageFallback(string filePath, string fileName)
    {
        var docs = new List<IngestedDocument>();

        using var archive = ZipFile.OpenRead(filePath);
        var slideEntries = archive.Entries
            .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                && !e.FullName.Contains("/_rels/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => GetSlideNumber(e.FullName))
            .ToList();

        var slideIndex = 1;
        foreach (var entry in slideEntries)
        {
            using var stream = entry.Open();
            var document = XDocument.Load(stream);
            var text = string.Join(" ", document
                .Descendants()
                .Where(e => e.Name.LocalName == "t")
                .Select(e => e.Value.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value)))
                .Trim();

            if (!string.IsNullOrWhiteSpace(text))
            {
                docs.Add(new IngestedDocument
                {
                    PageContent = $"[V\u0102N B\u1EA2N H\u01AF\u1EDA\u004E\u0047 D\u1EAAN]\n{text}",
                    Metadata = new Dictionary<string, object>
                    {
                        { "source", fileName },
                        { "slide", slideIndex },
                        { "type", "pptx_slide_text_fallback" },
                        { "content_kind", "instruction_text" }
                    }
                });
            }

            slideIndex++;
        }

        return docs;
    }

    private static int GetSlideNumber(string entryName)
    {
        var match = Regex.Match(entryName, @"slide(\d+)\.xml$", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var number)
            ? number
            : int.MaxValue;
    }

    private async Task<SlideContent> ExtractSlideContentAsync(
        SlidePart slidePart,
        string fileName,
        int slideIndex,
        VisionBudget visionBudget)
    {
        var content = new SlideContent();

        var slide = slidePart.Slide;
        var shapeTree = slide?.CommonSlideData?.ShapeTree;
        if (shapeTree == null) return content;

        foreach (var element in shapeTree.Elements())
        {
            await ProcessElementAsync(element, slidePart, content, fileName, slideIndex, visionBudget);
        }

        return content;
    }

    private async Task ProcessElementAsync(
        DocumentFormat.OpenXml.OpenXmlElement element,
        SlidePart slidePart,
        SlideContent content,
        string fileName,
        int slideIndex,
        VisionBudget visionBudget)
    {
        if (element is Shape shape)
        {
            var text = string.Join(" ", shape.Descendants<Text>().Select(t => t.Text)).Trim();
            if (!string.IsNullOrWhiteSpace(text)) content.InstructionalTexts.Add(text);
            return;
        }

        if (element is GraphicFrame graphicFrame)
        {
            var table = graphicFrame.Descendants<Table>().FirstOrDefault();
            if (table != null)
            {
                foreach (var row in table.Descendants<DocumentFormat.OpenXml.Drawing.TableRow>())
                {
                    var rowTexts = row.Descendants<DocumentFormat.OpenXml.Drawing.TableCell>()
                        .Select(cell => string.Join(" ", cell.Descendants<Text>().Select(t => t.Text)).Trim())
                        .Where(t => !string.IsNullOrWhiteSpace(t));
                    var rowString = string.Join(" | ", rowTexts);
                    if (!string.IsNullOrWhiteSpace(rowString)) content.InstructionalTexts.Add(rowString);
                }
            }
            else
            {
                var text = string.Join(" ", graphicFrame.Descendants<Text>().Select(t => t.Text)).Trim();
                if (!string.IsNullOrWhiteSpace(text)) content.InstructionalTexts.Add(text);
            }
            return;
        }

        if (element is Picture picture)
        {
            await ProcessPictureAsync(picture, slidePart, content, fileName, slideIndex, visionBudget);
            return;
        }

        if (element is DocumentFormat.OpenXml.Presentation.GroupShape groupShape)
        {
            foreach (var child in groupShape.Elements())
            {
                await ProcessElementAsync(child, slidePart, content, fileName, slideIndex, visionBudget);
            }
        }
    }

    private async Task ProcessPictureAsync(
        Picture picture,
        SlidePart slidePart,
        SlideContent content,
        string fileName,
        int slideIndex,
        VisionBudget visionBudget)
    {
        var relationshipId = picture.BlipFill?.Blip?.Embed?.Value;
        if (string.IsNullOrWhiteSpace(relationshipId))
        {
            return;
        }

        if (slidePart.GetPartById(relationshipId) is not ImagePart imagePart)
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
            _logger.LogWarning("Skipping image in {FileName} slide {Slide}: could not decode dimensions.", fileName, slideIndex);
            return;
        }

        content.ImageIndex++;

        var ocrText = await _ocrService.ExtractTextAsync(imageBytes);
        var role = ImageContentClassifier.Classify(width, height, ocrText, _options.MinVisionImageWidth, _options.MinVisionImageHeight);

        // --- Small image or short OCR (button/icon): inject inline marker into instruction text ---
        if (role == ImageRole.InlineIcon)
        {
            content.InstructionalTexts.Add(ImageContentClassifier.BuildInlineMarker(ocrText));
            return;
        }

        // --- Large image (screenshot/UI): add short placeholder to instruction text ---
        content.InstructionalTexts.Add(ImageContentClassifier.BuildScreenshotPlaceholder());

        // Build a separate image_ui_description chunk
        var metadata = new Dictionary<string, object>
        {
            { "source", fileName },
            { "slide", slideIndex },
            { "type", "pptx_slide_image" },
            { "content_kind", "image_ui_description" },
            { "image_index", content.ImageIndex },
            { "width", width },
            { "height", height },
            { "image_role", role.ToString().ToLower() }
        };

        string? caption = null;
        var visionAttempted = false;

        if (TryReserveVisionAttempt(width, height, fileName, slideIndex, visionBudget))
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
            content.ImageChunks.Add(new IngestedDocument
            {
                PageContent = $"[M\u00D4 T\u1EA2 GIAO DI\u1EC6N T\u1EEA H\u00CCNH \u1EA2NH]\n[H\u00ECnh \u1EA3nh tr\u00EAn slide: {string.Join("; ", descriptionParts)}]",
                Metadata = metadata
            });
        }
    }

    private bool TryReserveVisionAttempt(
        int width,
        int height,
        string fileName,
        int slideIndex,
        VisionBudget visionBudget)
    {
        if (!_options.EnableVisionCaption || string.IsNullOrWhiteSpace(_options.VisionApiUrl))
        {
            return false;
        }

        if (width < _options.MinVisionImageWidth || height < _options.MinVisionImageHeight)
        {
            _logger.LogDebug("Vision skipped small image in {FileName} slide {Slide}: {Width}x{Height}px.", fileName, slideIndex, width, height);
            return false;
        }

        if (_options.MaxVisionImagesPerSlide > 0 && visionBudget.SlideAttempts >= _options.MaxVisionImagesPerSlide)
        {
            _logger.LogInformation(
                "Vision skipped image in {FileName} slide {Slide}: per-slide limit {Limit} reached.",
                fileName,
                slideIndex,
                _options.MaxVisionImagesPerSlide);
            return false;
        }

        if (_options.MaxVisionImagesPerFile > 0 && visionBudget.FileAttempts >= _options.MaxVisionImagesPerFile)
        {
            _logger.LogInformation(
                "Vision skipped image in {FileName} slide {Slide}: per-file limit {Limit} reached.",
                fileName,
                slideIndex,
                _options.MaxVisionImagesPerFile);
            return false;
        }

        visionBudget.SlideAttempts++;
        visionBudget.FileAttempts++;
        return true;
    }

}
