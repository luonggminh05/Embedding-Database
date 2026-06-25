using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Drawing;
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
        using var presentationDocument = PresentationDocument.Open(filePath, false);
        var presentationPart = presentationDocument.PresentationPart;
        var fileVisionImageCount = 0;
        
        if (presentationPart?.Presentation?.SlideIdList != null)
        {
            int slideIndex = 1;
            foreach (var slideId in presentationPart.Presentation.SlideIdList.Elements<SlideId>())
            {
                if (slideId.RelationshipId == null) continue;
                
                var slidePart = (SlidePart)presentationPart.GetPartById(slideId.RelationshipId!);
                var slideText = await ExtractTextFromSlideAsync(slidePart, fileName, slideIndex, fileVisionImageCount);
                fileVisionImageCount += slideText.VisionImagesProcessed;
                
                if (!string.IsNullOrWhiteSpace(slideText.Text))
                {
                    docs.Add(new IngestedDocument
                    {
                        PageContent = slideText.Text,
                        Metadata = new Dictionary<string, object>
                        {
                            { "source", fileName },
                            { "slide", slideIndex },
                            { "type", "pptx_slide" }
                        }
                    });
                }
                slideIndex++;
            }
        }
        
        return docs;
    }

    private async Task<(string Text, int VisionImagesProcessed)> ExtractTextFromSlideAsync(
        SlidePart slidePart,
        string fileName,
        int slideIndex,
        int fileVisionImageCount)
    {
        var texts = new List<string>();
        var slideVisionImageCount = 0;
        
        var slide = slidePart.Slide;
        var shapeTree = slide?.CommonSlideData?.ShapeTree;
        if (shapeTree == null) return (string.Empty, 0);

        foreach (var element in shapeTree.Elements())
        {
            var processed = await ProcessElementAsync(element, slidePart, texts, fileName, slideIndex, slideVisionImageCount, fileVisionImageCount + slideVisionImageCount);
            slideVisionImageCount += processed;
        }

        return (string.Join("\n", texts), slideVisionImageCount);
    }

    private async Task<int> ProcessElementAsync(
        DocumentFormat.OpenXml.OpenXmlElement element,
        SlidePart slidePart,
        List<string> texts,
        string fileName,
        int slideIndex,
        int slideVisionImageCount,
        int fileVisionImageCount)
    {
        if (element is Shape shape)
        {
            var text = string.Join(" ", shape.Descendants<Text>().Select(t => t.Text)).Trim();
            if (!string.IsNullOrWhiteSpace(text)) texts.Add(text);
            return 0;
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
                    if (!string.IsNullOrWhiteSpace(rowString)) texts.Add(rowString);
                }
            }
            else
            {
                var text = string.Join(" ", graphicFrame.Descendants<Text>().Select(t => t.Text)).Trim();
                if (!string.IsNullOrWhiteSpace(text)) texts.Add(text);
            }
            return 0;
        }

        if (element is Picture picture)
        {
            return await ProcessPictureAsync(picture, slidePart, texts, fileName, slideIndex, slideVisionImageCount, fileVisionImageCount);
        }

        if (element is DocumentFormat.OpenXml.Presentation.GroupShape groupShape)
        {
            var processed = 0;
            foreach (var child in groupShape.Elements())
            {
                processed += await ProcessElementAsync(
                    child,
                    slidePart,
                    texts,
                    fileName,
                    slideIndex,
                    slideVisionImageCount + processed,
                    fileVisionImageCount + processed);
            }
            return processed;
        }

        return 0;
    }

    private async Task<int> ProcessPictureAsync(
        Picture picture,
        SlidePart slidePart,
        List<string> texts,
        string fileName,
        int slideIndex,
        int slideVisionImageCount,
        int fileVisionImageCount)
    {
        var relationshipId = picture.BlipFill?.Blip?.Embed?.Value;
        if (string.IsNullOrWhiteSpace(relationshipId))
        {
            return 0;
        }

        if (slidePart.GetPartById(relationshipId) is not ImagePart imagePart)
        {
            return 0;
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
            return 0;
        }

        var metadata = new Dictionary<string, object>
        {
            { "source", fileName },
            { "slide", slideIndex },
            { "type", "inline_image" },
            { "width", width },
            { "height", height }
        };

        var ocrText = await _ocrService.ExtractTextAsync(imageBytes);
        string? caption = null;
        var visionAttempted = false;

        if (CanAttemptVision(width, height, slideVisionImageCount, fileVisionImageCount, fileName, slideIndex))
        {
            visionAttempted = true;
            caption = await _visionCaptionService.GenerateCaptionAsync(imageBytes, metadata, ocrText);
        }

        var description = !string.IsNullOrWhiteSpace(caption)
            ? caption
            : !string.IsNullOrWhiteSpace(ocrText)
                ? $"OCR text: {ocrText}"
                : null;

        if (!string.IsNullOrWhiteSpace(description))
        {
            texts.Add($"[H\u00ECnh \u1EA3nh/N\u00FAt tr\u00EAn slide: {description}]");
        }

        return visionAttempted ? 1 : 0;
    }

    private bool CanAttemptVision(int width, int height, int slideVisionImageCount, int fileVisionImageCount, string fileName, int slideIndex)
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

        if (slideVisionImageCount >= _options.MaxVisionImagesPerSlide)
        {
            _logger.LogWarning("Vision skipped image in {FileName} slide {Slide}: per-slide limit reached ({Limit}).", fileName, slideIndex, _options.MaxVisionImagesPerSlide);
            return false;
        }

        if (fileVisionImageCount >= _options.MaxVisionImagesPerFile)
        {
            _logger.LogWarning("Vision skipped image in {FileName}: per-file limit reached ({Limit}).", fileName, _options.MaxVisionImagesPerFile);
            return false;
        }

        return true;
    }
}
