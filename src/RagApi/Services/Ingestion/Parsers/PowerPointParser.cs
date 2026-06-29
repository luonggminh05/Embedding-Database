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
                var slideText = await ExtractTextFromSlideAsync(slidePart, fileName, slideIndex, visionBudget);
                
                if (!string.IsNullOrWhiteSpace(slideText))
                {
                    yield return new IngestedDocument
                    {
                        PageContent = slideText,
                        Metadata = new Dictionary<string, object>
                        {
                            { "source", fileName },
                            { "slide", slideIndex },
                            { "type", "pptx_slide" }
                        }
                    };
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
                    PageContent = text,
                    Metadata = new Dictionary<string, object>
                    {
                        { "source", fileName },
                        { "slide", slideIndex },
                        { "type", "pptx_slide_text_fallback" }
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

    private async Task<string> ExtractTextFromSlideAsync(
        SlidePart slidePart,
        string fileName,
        int slideIndex,
        VisionBudget visionBudget)
    {
        var texts = new List<string>();
        
        
        var slide = slidePart.Slide;
        var shapeTree = slide?.CommonSlideData?.ShapeTree;
        if (shapeTree == null) return string.Empty;

        foreach (var element in shapeTree.Elements())
        {
            await ProcessElementAsync(element, slidePart, texts, fileName, slideIndex, visionBudget);
        }

        return string.Join("\n", texts);
    }

    private async Task ProcessElementAsync(
        DocumentFormat.OpenXml.OpenXmlElement element,
        SlidePart slidePart,
        List<string> texts,
        string fileName,
        int slideIndex,
        VisionBudget visionBudget)
    {
        if (element is Shape shape)
        {
            var text = string.Join(" ", shape.Descendants<Text>().Select(t => t.Text)).Trim();
            if (!string.IsNullOrWhiteSpace(text)) texts.Add(text);
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
                    if (!string.IsNullOrWhiteSpace(rowString)) texts.Add(rowString);
                }
            }
            else
            {
                var text = string.Join(" ", graphicFrame.Descendants<Text>().Select(t => t.Text)).Trim();
                if (!string.IsNullOrWhiteSpace(text)) texts.Add(text);
            }
            return;
        }

        if (element is Picture picture)
        {
            await ProcessPictureAsync(picture, slidePart, texts, fileName, slideIndex, visionBudget);
            return;
        }

    
        if (element is DocumentFormat.OpenXml.Presentation.GroupShape groupShape)
        {

            foreach (var child in groupShape.Elements())
            {
                await ProcessElementAsync(child, slidePart, texts, fileName, slideIndex, visionBudget);
            }
       
        }
    
    }

    private async Task ProcessPictureAsync(
        Picture picture,
        SlidePart slidePart,
        List<string> texts,
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

        if (TryReserveVisionAttempt(width, height, fileName, slideIndex, visionBudget))
        {
            visionAttempted = true;
            caption = await _visionCaptionService.GenerateCaptionAsync(imageBytes, metadata, ocrText);
        }

        var usefulCaption = VisionCaptionQuality.IsUseful(caption, ocrText) ? caption : null;
        var imageParts = new List<string>
        {
            $"dimensions: {width}x{height}px"
        };

        if (!string.IsNullOrWhiteSpace(ocrText))
        {
            imageParts.Add($"OCR text: {ocrText}");
        }

        if (!string.IsNullOrWhiteSpace(usefulCaption))
        {
            imageParts.Add($"Vision caption: {usefulCaption}");
        }
        else if (visionAttempted && string.IsNullOrWhiteSpace(ocrText))
        {
            imageParts.Add("Vision caption was empty or filtered as unreliable");
        }

        if (imageParts.Count > 1 || visionAttempted)
        {
            texts.Add($"[H\u00ECnh \u1EA3nh/N\u00FAt tr\u00EAn slide: {string.Join("; ", imageParts)}]");
      
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
