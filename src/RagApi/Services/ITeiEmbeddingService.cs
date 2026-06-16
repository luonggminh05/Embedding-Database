namespace RagApi.Services;

public interface ITeiEmbeddingService
{
    Task<IReadOnlyList<IReadOnlyList<float>>> EmbedTextsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
    Task<(IReadOnlyList<float> Embedding, bool CacheHit)> EmbedQueryCachedAsync(string query, CancellationToken cancellationToken);
}
