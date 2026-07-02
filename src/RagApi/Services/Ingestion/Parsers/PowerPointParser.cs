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
using RagApi.Services;

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

    private sealed record InstructionBlock(string Text, string InstructionRole, string? ActionKind, int StepCount);

    /// <summary>Holds separated content extracted from a single slide.</summary>
    private sealed class SlideContent
    {
        /// <summary>Text blocks from shapes, tables, and inline image markers.</summary>
        public List<InstructionBlock> InstructionBlocks { get; } = new();
        /// <summary>Standalone chunks for large images (OCR + Vision caption).</summary>
        public List<IngestedDocument> ImageChunks { get; } = new();
        /// <summary>Running counter for images on the current slide.</summary>
        public int ImageIndex { get; set; }
        public string? SlideTitle { get; set; }
        public string? ActionKind { get; set; }
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
        var allDocsForDuplicateCheck = new List<IngestedDocument>();
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
                
                // Emit instruction_text chunks (procedure steps are kept as complete blocks)
                foreach (var instructionBlock in slideContent.InstructionBlocks.Where(block => !string.IsNullOrWhiteSpace(block.Text)))
                {
                    var pageContent = string.IsNullOrEmpty(slideContent.SlideTitle)
                        ? $"[V\u0102N B\u1EA2N H\u01AF\u1EDA\u004E\u0047 D\u1EAAN]\n{instructionBlock.Text}"
                        : $"[SLIDE: {slideContent.SlideTitle}]\n[V\u0102N B\u1EA2N H\u01AF\u1EDA\u004E\u0047 D\u1EAAN]\n{instructionBlock.Text}";

                    var metadata = new Dictionary<string, object>
                    {
                        { "source", fileName },
                        { "slide", slideIndex },
                        { "type", "pptx_slide" },
                        { "content_kind", "instruction_text" },
                        { "instruction_role", instructionBlock.InstructionRole }
                    };

                    if (!string.IsNullOrEmpty(slideContent.SlideTitle))
                    {
                        metadata["slide_title"] = slideContent.SlideTitle;
                    }

                    var actionKind = instructionBlock.ActionKind ?? slideContent.ActionKind;
                    if (!string.IsNullOrEmpty(actionKind))
                    {
                        metadata["action_kind"] = actionKind;
                    }

                    if (instructionBlock.StepCount > 0)
                    {
                        metadata["step_count"] = instructionBlock.StepCount;
                    }

                    var instructionDoc = new IngestedDocument
                    {
                        PageContent = pageContent,
                        Metadata = metadata
                    };

                    allDocsForDuplicateCheck.Add(instructionDoc);
                    yield return instructionDoc;
                }

                // Emit each image_ui_description chunk separately
                foreach (var imageChunk in slideContent.ImageChunks)
                {
                    if (!string.IsNullOrEmpty(slideContent.SlideTitle))
                    {
                        imageChunk.Metadata["slide_title"] = slideContent.SlideTitle;
                    }
                    if (!string.IsNullOrEmpty(slideContent.ActionKind))
                    {
                        imageChunk.Metadata["action_kind"] = slideContent.ActionKind;
                    }

                    allDocsForDuplicateCheck.Add(imageChunk);
                    yield return imageChunk;
                }

                slideIndex++;
            }
        }

        NearDuplicateDetector.DetectAndLog(allDocsForDuplicateCheck, fileName, _logger);
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
            string text;
            var paragraphs = document.Descendants().Where(e => e.Name.LocalName == "p").ToList();
            if (paragraphs.Count > 0)
            {
                var paragraphTexts = new List<string>();
                foreach (var p in paragraphs)
                {
                    var pText = string.Concat(p.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value));
                    if (!string.IsNullOrWhiteSpace(pText))
                    {
                        paragraphTexts.Add(CleanPptText(pText));
                    }
                }
                text = string.Join("\n", paragraphTexts);
            }
            else
            {
                text = string.Concat(document.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value)).Trim();
            }

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

    private static bool IsStepLine(string line) =>
        Regex.IsMatch(line, @"^\s*(?:Bước|Buoc|Step|B)\s*\d+\b", RegexOptions.IgnoreCase);

    private static string NormalizeForActionDetection(string text) => ActionKindClassifier.NormalizeText(text);

    private static string? DetermineActionKind(string? title) => ActionKindClassifier.DetermineActionKind(title);
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

        // Scan shapes for title
        string? detectedTitle = null;
        foreach (var element in shapeTree.Elements())
        {
            if (element is Shape shape)
            {
                var text = GetCleanText(shape);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        if (IsStepLine(line)) continue;
                        var normLine = NormalizeForActionDetection(line);
                        if (normLine.Contains("to trinh nghiep vu"))
                        {
                            detectedTitle = line.Trim();
                            break;
                        }
                    }
                }
            }
            if (detectedTitle != null) break;
        }

        if (detectedTitle == null)
        {
            foreach (var element in shapeTree.Elements())
            {
                if (element is Shape shape)
                {
                    var text = GetCleanText(shape);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var line in lines)
                        {
                            if (IsStepLine(line)) continue;
                            detectedTitle = line.Trim();
                            break;
                        }
                        if (detectedTitle != null) break;
                    }
                }
            }
        }

        content.SlideTitle = detectedTitle;
        content.ActionKind = DetermineActionKind(detectedTitle);

        foreach (var element in shapeTree.Elements())
        {
            await ProcessElementAsync(element, slidePart, content, fileName, slideIndex, visionBudget);
        }

        return content;
    }

    private static string GetCleanText(DocumentFormat.OpenXml.OpenXmlElement element)
    {
        var paragraphs = element.Descendants<DocumentFormat.OpenXml.Drawing.Paragraph>().ToList();
        if (paragraphs.Count == 0)
        {
            var text = string.Concat(element.Descendants<Text>().Select(t => t.Text));
            return CleanPptText(text);
        }

        var paragraphTexts = new List<string>();
        foreach (var p in paragraphs)
        {
            var pText = string.Concat(p.Descendants<Text>().Select(t => t.Text));
            if (!string.IsNullOrWhiteSpace(pText))
            {
                paragraphTexts.Add(CleanPptText(pText));
            }
        }
        return CleanPptText(string.Join("\n", paragraphTexts));
    }

    private static string CleanPptText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        
        var pattern = @"[ \t]*\t[ \t]*| {8,}";
        normalized = Regex.Replace(normalized, pattern, match =>
        {
            var index = match.Index;
            var textBefore = normalized.Substring(0, index).TrimEnd();
            
            var last25 = textBefore.Length >= 25 
                ? textBefore.Substring(textBefore.Length - 25) 
                : textBefore;
            
            bool hasNutNearby = last25.Contains("nút", StringComparison.OrdinalIgnoreCase) || 
                                last25.Contains("nut", StringComparison.OrdinalIgnoreCase);
            
            return hasNutNearby ? " " : " nút ";
        });

        normalized = Regex.Replace(normalized, @"[ \f\v]+", " ");
        normalized = Regex.Replace(normalized, @" *\n *", "\n");
        return normalized.Trim();
    }

    private static void AddInstructionText(SlideContent content, string text)
    {
        var cleaned = CleanPptText(text);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return;
        }

        var procedureBlocks = SplitProcedureBlocks(cleaned);
        if (procedureBlocks.Count == 0)
        {
            var detailAction = ActionKindClassifier.DetermineActionKind(cleaned) ?? content.ActionKind;
            content.InstructionBlocks.Add(new InstructionBlock(cleaned, "ui_details", detailAction, 0));
            return;
        }

        foreach (var block in procedureBlocks)
        {
            var stepCount = CountSteps(block);
            var actionKind = ActionKindClassifier.DetermineActionKind(block) ?? content.ActionKind;
            content.InstructionBlocks.Add(new InstructionBlock(block, "procedure_steps", actionKind, stepCount));
        }
    }

    private static List<string> SplitProcedureBlocks(string text)
    {
        if (CountSteps(text) == 0)
        {
            return [];
        }

        var starts = Regex.Matches(text, @"\b(?:Bước|Buoc|B)\s*1\s*:", RegexOptions.IgnoreCase)
            .Select(match => match.Index)
            .ToList();

        if (starts.Count <= 1)
        {
            return [text.Trim()];
        }

        var blocks = new List<string>();
        for (var i = 0; i < starts.Count; i++)
        {
            var start = starts[i];
            var end = i + 1 < starts.Count ? starts[i + 1] : text.Length;
            var block = text[start..end].Trim();
            if (!string.IsNullOrWhiteSpace(block))
            {
                blocks.Add(block);
            }
        }

        return blocks;
    }

    private static int CountSteps(string text) =>
        Regex.Matches(text, @"\b(?:Bước|Buoc|B)\s*\d+\s*:", RegexOptions.IgnoreCase).Count;

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
            var text = GetCleanText(shape);
            if (!string.IsNullOrWhiteSpace(text)) AddInstructionText(content, text);
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
                        .Select(cell => GetCleanText(cell))
                        .Where(t => !string.IsNullOrWhiteSpace(t));
                    var rowString = string.Join(" | ", rowTexts);
                    if (!string.IsNullOrWhiteSpace(rowString)) AddInstructionText(content, rowString);
                }
            }
            else
            {
                var text = GetCleanText(graphicFrame);
                if (!string.IsNullOrWhiteSpace(text)) AddInstructionText(content, text);
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
            var marker = ImageContentClassifier.BuildInlineMarker(ocrText);
            if (!string.IsNullOrEmpty(marker))
            {
                AddInstructionText(content, marker);
            }
            return;
        }

        // --- Large image (screenshot/UI): add short placeholder to instruction text ---
        AddInstructionText(content, ImageContentClassifier.BuildScreenshotPlaceholder());

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
