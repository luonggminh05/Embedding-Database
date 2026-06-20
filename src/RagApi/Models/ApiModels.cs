using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RagApi.Models;

public sealed class EmbedRequest
{
    [Required]
    [MinLength(1)]
    [JsonPropertyName("texts")]
    public IReadOnlyList<string> Texts { get; init; } = [];
}

public sealed record EmbedResponse(
    [property: JsonPropertyName("embeddings")] IReadOnlyList<IReadOnlyList<float>> Embeddings);

public sealed class AddDocumentsRequest
{
    [Required]
    [MinLength(1)]
    [JsonPropertyName("documents")]
    public IReadOnlyList<string> Documents { get; init; } = [];

    [JsonPropertyName("metadatas")]
    public IReadOnlyList<JsonElement>? Metadatas { get; init; }

    [Required]
    [MinLength(1)]
    [JsonPropertyName("ids")]
    public IReadOnlyList<string> Ids { get; init; } = [];
}

public sealed record AddDocumentsResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("count")] int Count);

public sealed class SearchRequest
{
    [Required]
    [MinLength(1)]
    [JsonPropertyName("query")]
    public string Query { get; init; } = string.Empty;

    [JsonPropertyName("top_k")]
    public int TopK { get; init; } = 5;
}

public sealed record Citation(
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("page")] string? Page,
    [property: JsonPropertyName("chunk_id")] string ChunkId,
    [property: JsonPropertyName("score")] double Score);

public sealed record SearchResponse(
    [property: JsonPropertyName("ids")] IReadOnlyList<IReadOnlyList<string>> Ids,
    [property: JsonPropertyName("documents")] IReadOnlyList<IReadOnlyList<string>> Documents,
    [property: JsonPropertyName("metadatas")] IReadOnlyList<IReadOnlyList<JsonElement>> Metadatas,
    [property: JsonPropertyName("distances")] IReadOnlyList<IReadOnlyList<double>> Distances,
    [property: JsonPropertyName("citations")] IReadOnlyList<IReadOnlyList<Citation>> Citations);

public sealed record HealthResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string Message);
