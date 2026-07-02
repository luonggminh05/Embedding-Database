using Microsoft.Extensions.Options;
using RagApi.Options;
using Tesseract;

namespace RagApi.Services.Ingestion;

public sealed class TesseractOcrService : IOcrService
{
    private readonly IngestionOptions _options;
    private readonly ILogger<TesseractOcrService> _logger;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TesseractEngine> _engines = new();

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

        if (ImageUtilities.TryGetDimensions(imageBytes, out var width, out var height) && ImageUtilities.ShouldSkipOcr(width, height))
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

            var languages = GetAvailableLanguageCandidates(tessDataPath);
            if (languages.Count == 0)
            {
                _logger.LogWarning(
                    "No supported Tesseract traineddata files found in {Path}. Expected eng.traineddata and/or vie.traineddata.",
                    tessDataPath);
                return null;
            }

            var variants = ImageUtilities.PrepareOcrVariants(imageBytes);
            foreach (var variant in variants)
            {
                foreach (var language in languages)
                {
                    var text = TryExtractText(tessDataPath, language, variant);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }

            return null;
        }, cancellationToken);
    }

    private static IReadOnlyList<string> GetAvailableLanguageCandidates(string tessDataPath)
    {
        var hasEnglish = File.Exists(Path.Combine(tessDataPath, "eng.traineddata"));
        var hasVietnamese = File.Exists(Path.Combine(tessDataPath, "vie.traineddata"));

        if (hasEnglish && hasVietnamese)
        {
            return new[] { "eng+vie", "vie", "eng" };
        }

        if (hasVietnamese)
        {
            return new[] { "vie" };
        }

        if (hasEnglish)
        {
            return new[] { "eng" };
        }

        return Array.Empty<string>();
    }

    private string? TryExtractText(string tessDataPath, string language, byte[] imageBytes)
    {
        try
        {
            var engine = _engines.GetOrAdd(language, lang =>
            {
                var newEngine = new TesseractEngine(tessDataPath, lang, EngineMode.Default);
                newEngine.SetVariable("preserve_interword_spaces", "1");
                return newEngine;
            });

            lock (engine)
            {
                using var image = Pix.LoadFromMemory(imageBytes);
                using var page = engine.Process(image);
                var text = page.GetText()?.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }
                var cleaned = TextArtifactCleaner.Clean(text);
                return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OCR failed with language {Language} using tessdata path {Path}.", language, tessDataPath);
            return null;
        }  
    }
}