namespace RagApi.Services.Ingestion;

public interface IVisionCaptionService
{
    Task<string?> GenerateCaptionAsync(
        byte[] imageBytes,
        Dictionary<string, object> metadata,
        string? ocrText,
        CancellationToken cancellationToken = default);
}
