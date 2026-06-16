using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using RagApi.Data;
using RagApi.Models;
using RagApi.Services;

namespace RagApi.Controllers;

[ApiController]
[Route("db")]
public sealed class DatabaseController : ControllerBase
{
    private readonly ITeiEmbeddingService _embeddingService;
    private readonly ISqlDocumentRepository _repository;
    private readonly ILogger<DatabaseController> _logger;

    public DatabaseController(
        ITeiEmbeddingService embeddingService,
        ISqlDocumentRepository repository,
        ILogger<DatabaseController> logger)
    {
        _embeddingService = embeddingService;
        _repository = repository;
        _logger = logger;
    }

    [HttpPost("add")]
    public async Task<ActionResult<AddDocumentsResponse>> Add([FromBody] AddDocumentsRequest request, CancellationToken cancellationToken)
    {
        if (request.Ids.Count != request.Documents.Count)
        {
            return BadRequest("ids count must match documents count.");
        }

        if (request.Metadatas is not null && request.Metadatas.Count != request.Documents.Count)
        {
            return BadRequest("metadatas count must match documents count when provided.");
        }

        try
        {
            var embeddings = await _embeddingService.EmbedTextsAsync(request.Documents, cancellationToken);
            await _repository.UpsertDocumentsAsync(request, embeddings, cancellationToken);
            return Ok(new AddDocumentsResponse("success", request.Documents.Count));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in /db/add.");
            return StatusCode(StatusCodes.Status500InternalServerError, new { detail = ex.Message });
        }
    }

    [HttpPost("search")]
    public async Task<ActionResult<SearchResponse>> Search([FromBody] SearchRequest request, CancellationToken cancellationToken)
    {

        try
        {
            var totalStopwatch = Stopwatch.StartNew();
            var teiStopwatch = Stopwatch.StartNew();
            var (embedding, cacheHit) = await _embeddingService.EmbedQueryCachedAsync(request.Query, cancellationToken);
            teiStopwatch.Stop();

            var sqlStopwatch = Stopwatch.StartNew();
            var response = await _repository.SearchAsync(request.Query, embedding, request.TopK, cancellationToken);
            sqlStopwatch.Stop();

            _logger.LogInformation(
                "Search Performance - TEI: {TeiSeconds:F3}s | SQL: {SqlSeconds:F3}s | Total: {TotalSeconds:F3}s | CacheHit: {CacheHit}",
                teiStopwatch.Elapsed.TotalSeconds,
                sqlStopwatch.Elapsed.TotalSeconds,
                totalStopwatch.Elapsed.TotalSeconds,
                cacheHit);

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in /db/search.");
            return StatusCode(StatusCodes.Status500InternalServerError, new { detail = ex.Message });
        }
    }

}
