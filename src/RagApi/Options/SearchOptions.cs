namespace RagApi.Options;

public sealed class SearchOptions
{
    public int TopKMax { get; set; } = 50;
    public int ChatTopK { get; set; } = 3;
    public int VectorCandidateCount { get; set; } = 200;
    public double RrfConstant { get; set; } = 60;
    public int QueryEmbeddingCacheSize { get; set; } = 1024;
    public int QueryEmbeddingCacheTtlSeconds { get; set; } = 3600;
}
