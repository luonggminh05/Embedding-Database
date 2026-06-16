using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using RagApi.Options;

namespace RagApi.Services;

public sealed class TeiEmbeddingService : ITeiEmbeddingService
{
    private const int BatchSize = 32;
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TeiEmbeddingService> _logger;
    private readonly SearchOptions _searchOptions;
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<float>>>> _inflightQueries = new();

    public TeiEmbeddingService(
        HttpClient httpClient,
        IMemoryCache cache,
        IOptions<TeiOptions> teiOptions,
        IOptions<SearchOptions> searchOptions,
        ILogger<TeiEmbeddingService> logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _logger = logger;
        _searchOptions = searchOptions.Value;
        _httpClient.BaseAddress = new Uri(teiOptions.Value.Url.TrimEnd('/') + "/");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<IReadOnlyList<IReadOnlyList<float>>> EmbedTextsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        var allEmbeddings = new List<IReadOnlyList<float>>(texts.Count);

        for (var offset = 0; offset < texts.Count; offset += BatchSize)
        {
            var batch = texts.Skip(offset).Take(BatchSize).ToArray();
            using var response = await _httpClient.PostAsJsonAsync("embed", new
            {
                inputs = batch,
                normalize = true,
                truncate = true
            }, cancellationToken);

            response.EnsureSuccessStatusCode();

            var embeddings = await response.Content.ReadFromJsonAsync<List<List<float>>>(cancellationToken: cancellationToken);
            if (embeddings is null || embeddings.Count != batch.Length)
            {
                throw new InvalidOperationException("TEI returned an invalid embedding response.");
            }

            allEmbeddings.AddRange(embeddings);
        }

        return allEmbeddings;
    }

    public async Task<(IReadOnlyList<float> Embedding, bool CacheHit)> EmbedQueryCachedAsync(string query, CancellationToken cancellationToken)
    {
        if (_searchOptions.QueryEmbeddingCacheSize <= 0)
        {
            return ((await EmbedTextsAsync([query], cancellationToken))[0], false);
        }

        var cacheKey = "query-embedding:" + NormalizeCacheKey(query);
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<float>? cached) && cached is not null)
        {
            return (cached, true);
        }

        var newLazyTask = new Lazy<Task<IReadOnlyList<float>>>(async () =>
        {
            var embedding = (await EmbedTextsAsync([query], cancellationToken))[0];
            _cache.Set(cacheKey, embedding, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(Math.Max(1, _searchOptions.QueryEmbeddingCacheTtlSeconds)),
                Size = 1
            });
            return embedding;
        });

        var lazyTask = _inflightQueries.GetOrAdd(cacheKey, newLazyTask);
        var joinedInflightRequest = !ReferenceEquals(lazyTask, newLazyTask);

        try
        {
            var embedding = await lazyTask.Value;
            return (embedding, joinedInflightRequest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching query embedding from TEI.");
            throw;
        }
        finally
        {
            if (_inflightQueries.TryGetValue(cacheKey, out var currentLazyTask) && ReferenceEquals(currentLazyTask, lazyTask))
            {
                _inflightQueries.TryRemove(cacheKey, out _);
            }
        }
    }

    private static string NormalizeCacheKey(string text) => string.Join(' ', text.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
