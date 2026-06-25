using Microsoft.Extensions.Options;
using RagApi.Options;
using Tesseract;

namespace RagApi.Services.Ingestion;

public sealed class TesseractOcrService : IOcrService
{
    private readonly IngestionOptions _options;
    private readonly ILogger<TesseractOcrService> _logger;

    public TesseractOcrService(IOptions<IngestionOptions> options, ILogger<TesseractOcrService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task<string?> ExtractTextAsync(byte[] imageBytes, CancellationToken cancellationToken = default)
    {
        if (!_options.EnableOcr || imageBytes.Length == 0)
        {
            return Task.FromResult<string?>(null);
        }

        var tessDataPath = _options.TessdataPath;
        if (!Path.IsPathRooted(tessDataPath))
        {
            tessDataPath = Path.Combine(Directory.GetCurrentDirectory(), tessDataPath);
        }

        if (!Directory.Exists(tessDataPath))
        {
            _logger.LogWarning("Tessdata directory not found at {Path}. Skipping OCR.", tessDataPath);
            return Task.FromResult<string?>(null);
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = TryExtractText(tessDataPath, "eng+vie", imageBytes);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            _logger.LogDebug("OCR returned no text or failed with eng+vie. Trying eng.");
            return TryExtractText(tessDataPath, "eng", imageBytes);
        }, cancellationToken);
    }

    private string? TryExtractText(string tessDataPath, string language, byte[] imageBytes)
    {
        try
        {
            using var engine = new TesseractEngine(tessDataPath, language, EngineMode.Default);
            using var image = Pix.LoadFromMemory(imageBytes);
            using var page = engine.Process(image);
            var text = page.GetText()?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OCR failed with language {Language}.", language);
            return null;
        }
    }
}
