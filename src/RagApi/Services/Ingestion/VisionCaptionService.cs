using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RagApi.Options;

namespace RagApi.Services.Ingestion;

public sealed class VisionCaptionService : IVisionCaptionService
{
    private readonly HttpClient _httpClient;
    private readonly IngestionOptions _options;
    private readonly ILogger<VisionCaptionService> _logger;

    public VisionCaptionService(HttpClient httpClient, IOptions<IngestionOptions> options, ILogger<VisionCaptionService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string?> GenerateCaptionAsync(
        byte[] imageBytes,
        Dictionary<string, object> metadata,
        string? ocrText,
        CancellationToken cancellationToken = default)
    {
        if (!_options.EnableVisionCaption || string.IsNullOrWhiteSpace(_options.VisionApiUrl))
        {
            return null;
        }

        if (!ImageUtilities.TryGetDimensions(imageBytes, out var width, out var height))
        {
            _logger.LogWarning("Vision skipped image from {Source}: could not decode dimensions.", GetMetadata(metadata, "source"));
            return null;
        }

        if (width < _options.MinVisionImageWidth || height < _options.MinVisionImageHeight)
        {
            _logger.LogDebug(
                "Vision skipped small image from {Source}: {Width}x{Height}px.",
                GetMetadata(metadata, "source"),
                width,
                height);
            return null;
        }

        var compressedBytes = ImageUtilities.CompressForVision(imageBytes);
        var imageBase64 = Convert.ToBase64String(compressedBytes);
        var prompt = BuildPrompt(ocrText);

        var payload = new
        {
            model = _options.VisionModel,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = prompt },
                        new
                        {
                            type = "image_url",
                            image_url = new
                            {
                                url = $"data:image/jpeg;base64,{imageBase64}"
                            }
                        }
                    }
                }
            },
            temperature = 0,
            max_tokens = 1000
        };

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.VisionTimeoutSeconds));

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.VisionApiUrl)
            {
                Content = JsonContent.Create(payload)
            };

            if (!string.IsNullOrWhiteSpace(_options.VisionApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.VisionApiKey);
            }

            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Vision failed for {Source}: HTTP {StatusCode}.", GetMetadata(metadata, "source"), response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token);
            var caption = json.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString()
                ?.Trim();

            if (!string.IsNullOrWhiteSpace(caption))
            {
                _logger.LogInformation("Vision caption generated for {Source} ({Width}x{Height}px).", GetMetadata(metadata, "source"), width, height);
                return caption;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Vision timed out for {Source} after {TimeoutSeconds}s.", GetMetadata(metadata, "source"), _options.VisionTimeoutSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vision failed for {Source}.", GetMetadata(metadata, "source"));
        }

        return null;
    }

    private static string BuildPrompt(string? ocrText)
    {
        var prompt =
            "<image>\n" +
            "B\u1EA1n l\u00E0 m\u1ED9t tr\u1EE3 l\u00FD th\u00F4ng minh chuy\u00EAn \u0111\u1ECDc t\u00E0i li\u1EC7u h\u01B0\u1EDBng d\u1EABn s\u1EED d\u1EE5ng v\u00E0 chuy\u00EAn gia ph\u00E2n t\u00EDch \u1EA3nh giao di\u1EC7n ph\u1EA7n m\u1EC1m. " +
            "H\u00E3y m\u00F4 t\u1EA3 th\u1EADt chi ti\u1EBFt h\u00ECnh \u1EA3nh n\u00E0y b\u1EB1ng ti\u1EBFng Vi\u1EC7t \u0111\u1EC3 l\u00E0m d\u1EEF li\u1EC7u cho h\u1EC7 th\u1ED1ng t\u00ECm ki\u1EBFm RAG. " +
            "Y\u00EAu c\u1EA7u: " +
            "1. Tr\u00EDch xu\u1EA5t tr\u1ECDn v\u1EB9n v\u0103n b\u1EA3n h\u01B0\u1EDBng d\u1EABn, quy tr\u00ECnh, c\u00E1c b\u01B0\u1EDBc th\u1EF1c hi\u1EC7n ho\u1EB7c ch\u00FA th\u00EDch c\u00F3 trong \u1EA3nh. " +
            "2. Li\u1EC7t k\u00EA \u0111\u1EA7y \u0111\u1EE7 v\u00E0 chi ti\u1EBFt t\u00EAn c\u1EE7a c\u00E1c \u00F4 nh\u1EADp li\u1EC7u, nh\u00E3n, n\u00FAt b\u1EA5m, \u0111i\u1EC1u ki\u1EC7n t\u00ECm ki\u1EBFm, d\u1EEF li\u1EC7u b\u1EA3ng. " +
            "Kh\u00F4ng t\u00F3m t\u1EAFt qua loa, ph\u1EA3i gi\u1EEF nguy\u00EAn c\u00E1c thu\u1EADt ng\u1EEF nghi\u1EC7p v\u1EE5 v\u00E0 lu\u1ED3ng thao t\u00E1c.";

        if (!string.IsNullOrWhiteSpace(ocrText))
        {
            prompt += $"\nOCR text already detected: {ocrText[..Math.Min(ocrText.Length, 1000)]}";
        }

        return prompt;
    }

    private static object? GetMetadata(Dictionary<string, object> metadata, string key) =>
        metadata.TryGetValue(key, out var value) ? value : null;
}
