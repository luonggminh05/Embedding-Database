using Docnet.Core;
using Docnet.Core.Models;
using Docnet.Core.Readers;
using Microsoft.Extensions.Options;
using RagApi.Options;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace RagApi.Services.Ingestion.Parsers;

public class PdfParser : IDocumentParser
{
    private readonly IOcrService _ocrService;
    private readonly IVisionCaptionService _visionCaptionService;
    private readonly IngestionOptions _options;
    private readonly ILogger<PdfParser> _logger;

    public PdfParser(
        IOcrService ocrService,
        IVisionCaptionService visionCaptionService,
        IOptions<IngestionOptions> options,
        ILogger<PdfParser> logger)
    {
        _ocrService = ocrService;
        _visionCaptionService = visionCaptionService;
        _options = options.Value;
        _logger = logger;
    }

    public bool CanParse(string extension) => extension == ".pdf";

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
        using var pdfDocument = PdfDocument.Open(filePath);
        
        IDocLib? docnetLib = null;
        IDocReader? docReader = null;
        try
        {
            var imageIndex = 0;

            foreach (var page in pdfDocument.GetPages())
            {
                var words = page.GetWords();
                var text = page.Text;

                // Heuristic for scanned page: less than 10 words extracted by PdfPig
                if (words.Count() < 10 || string.IsNullOrWhiteSpace(text))
                {
                    _logger.LogInformation("PDF page {Page} in {FileName} appears to be scanned or contains little text. Using render-page fallback.", page.Number, fileName);
                    
                    if (docnetLib == null)
                    {
                        docnetLib = DocLib.Instance;
                        docReader = docnetLib.GetDocReader(filePath, new PageDimensions(2.0)); // 2.0 scaling
                    }

                    // Docnet uses 0-based page index
                    using var docReaderPage = docReader!.GetPageReader(page.Number - 1);
                    var width = docReaderPage.GetPageWidth();
                    var height = docReaderPage.GetPageHeight();
                    var rawBytes = docReaderPage.GetImage(); // Returns BGRA byte array

                    // Convert raw BGRA to PNG using SkiaSharp in a non-async method to allow unsafe
                    var pageImageBytes = ConvertBgraToPng(rawBytes, width, height);

                    var ocrText = await _ocrService.ExtractTextAsync(pageImageBytes);

                    if (!string.IsNullOrWhiteSpace(ocrText))
                    {
                        yield return new IngestedDocument
                        {
                            PageContent = $"[V\u0102N B\u1ea2N H\u01af\u1edaNG D\u1eaaN]\n{ocrText}",
                            Metadata = new Dictionary<string, object>
                            {
                                { "source", fileName },
                                { "page", page.Number },
                                { "type", "pdf_scanned_text" },
                                { "content_kind", "instruction_text" }
                            }
                        };
                    }

                    // Optional: Run Vision on the whole page to get UI description
                    if (_options.EnableVisionCaption && !string.IsNullOrWhiteSpace(_options.VisionApiUrl))
                    {
                        imageIndex++;
                        var metadata = new Dictionary<string, object>
                        {
                            { "source", fileName },
                            { "page", page.Number },
                            { "type", "pdf_scanned_image" },
                            { "content_kind", "image_ui_description" },
                            { "image_index", imageIndex },
                            { "width", width },
                            { "height", height },
                            { "image_role", "scanned_page" }
                        };

                        var caption = await _visionCaptionService.GenerateCaptionAsync(pageImageBytes, metadata, ocrText);
                        var usefulCaption = VisionCaptionQuality.IsUseful(caption, ocrText) ? caption : null;

                        if (!string.IsNullOrWhiteSpace(usefulCaption))
                        {
                            yield return new IngestedDocument
                            {
                                PageContent = $"[M\u00D4 T\u1EA2 GIAO DI\u1EC6N T\u1EEA H\u00CCNH \u1EA2NH]\n[Trang t\u00E0i li\u1EC7u scan: Vision caption: {usefulCaption}]",
                                Metadata = metadata
                            };
                        }
                    }
                }
                else
                {
                    // Native PDF text extraction
                    var iconMarkers = new List<string>();
                    var imageChunks = new List<IngestedDocument>();

                    foreach (var pdfImage in page.GetImages())
                    {
                        var imageBytesLength = pdfImage.RawBytes.Length;
                        if (imageBytesLength > 0)
                        {
                            var imageBytes = pdfImage.RawBytes.ToArray();
                            // Attempt to get image format or directly pass to OCR. 
                            // PdfPig sometimes returns raw bytes. Let's assume OCR service handles it or we decode it.
                            // If OCR service fails on raw PDF image formats (like CMYK jpeg or JP2), we might need ImageSharp.
                            // But let's pass it and catch if it fails.
                            try
                            {
                                if (!ImageUtilities.TryGetDimensions(imageBytes, out var width, out var height))
                                {
                                    // Try to get width/height from PdfImage
                                    width = pdfImage.WidthInSamples;
                                    height = pdfImage.HeightInSamples;
                                }

                                if (width <= 0 || height <= 0) continue;
                                imageIndex++;

                                var ocrText = await _ocrService.ExtractTextAsync(imageBytes);
                                var role = ImageContentClassifier.Classify(width, height, ocrText, _options.MinVisionImageWidth, _options.MinVisionImageHeight);

                                if (role == ImageRole.InlineIcon)
                                {
                                    iconMarkers.Add(ImageContentClassifier.BuildInlineMarker(ocrText));
                                }
                                else
                                {
                                    var metadata = new Dictionary<string, object>
                                    {
                                        { "source", fileName },
                                        { "page", page.Number },
                                        { "type", "pdf_image" },
                                        { "content_kind", "image_ui_description" },
                                        { "image_index", imageIndex },
                                        { "width", width },
                                        { "height", height },
                                        { "image_role", role.ToString().ToLower() }
                                    };

                                    string? caption = null;
                                    var visionAttempted = false;

                                    if (_options.EnableVisionCaption && !string.IsNullOrWhiteSpace(_options.VisionApiUrl))
                                    {
                                        visionAttempted = true;
                                        caption = await _visionCaptionService.GenerateCaptionAsync(imageBytes, metadata, ocrText);
                                    }

                                    var usefulCaption = VisionCaptionQuality.IsUseful(caption, ocrText) ? caption : null;
                                    var descriptionParts = new List<string> { $"dimensions: {width}x{height}px" };

                                    if (!string.IsNullOrWhiteSpace(ocrText)) descriptionParts.Add($"OCR text: {ocrText}");
                                    if (!string.IsNullOrWhiteSpace(usefulCaption)) descriptionParts.Add($"Vision caption: {usefulCaption}");
                                    else if (visionAttempted && string.IsNullOrWhiteSpace(ocrText)) descriptionParts.Add("Vision caption was empty or filtered as unreliable");

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
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to process image on page {Page} of {FileName}", page.Number, fileName);
                            }
                        }
                    }

                    // Emit instruction_text chunk for this page
                    var instructionText = text;
                    if (iconMarkers.Count > 0)
                    {
                        instructionText += $"\n\n[Danh s\u00E1ch n\u00FAt/icon xu\u1EA5t hi\u1EC7n trong trang: {string.Join(", ", iconMarkers)}]";
                    }

                    yield return new IngestedDocument
                    {
                        PageContent = $"[V\u0102N B\u1ea2N H\u01af\u1edaNG D\u1eaaN]\n{instructionText}",
                        Metadata = new Dictionary<string, object>
                        {
                            { "source", fileName },
                            { "page", page.Number },
                            { "type", "pdf_text" },
                            { "content_kind", "instruction_text" }
                        }
                    };

                    // Emit image chunks
                    foreach (var imageChunk in imageChunks)
                    {
                        yield return imageChunk;
                    }
                }
            }
        }
        finally
        {
            docReader?.Dispose();
        }
    }

    private static byte[] ConvertBgraToPng(byte[] rawBytes, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        unsafe
        {
            fixed (byte* p = rawBytes)
            {
                bitmap.SetPixels((IntPtr)p);
            }
        }
        using var skImage = SKImage.FromBitmap(bitmap);
        using var data = skImage.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
