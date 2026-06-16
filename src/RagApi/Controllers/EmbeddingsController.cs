using Microsoft.AspNetCore.Mvc;
using RagApi.Models;
using RagApi.Services;

namespace RagApi.Controllers;

[ApiController]
public sealed class EmbeddingsController : ControllerBase
{
    private readonly ITeiEmbeddingService _embeddingService;
    private readonly ILogger<EmbeddingsController> _logger;

    public EmbeddingsController(ITeiEmbeddingService embeddingService, ILogger<EmbeddingsController> logger)
    {
        _embeddingService = embeddingService;
        _logger = logger;
    }

    [HttpPost("embed")]
    public async Task<ActionResult<EmbedResponse>> Embed([FromBody] EmbedRequest request, CancellationToken cancellationToken)
    {

        try
        {
            var embeddings = await _embeddingService.EmbedTextsAsync(request.Texts, cancellationToken);
            return Ok(new EmbedResponse(embeddings));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in /embed.");
            return StatusCode(StatusCodes.Status500InternalServerError, new { detail = ex.Message });
        }
    }
}
