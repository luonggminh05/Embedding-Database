namespace RagApi.Services.Ingestion;

public interface IOcrService
{
    Task<string?> ExtractTextAsync(byte[] imageBytes, CancellationToken cancellationToken = default);
}
