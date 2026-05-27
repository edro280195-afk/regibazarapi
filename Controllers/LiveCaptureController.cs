using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/live")]
[Authorize]
public class LiveCaptureController : ControllerBase
{
    private readonly ILiveCaptureService _svc;

    public LiveCaptureController(ILiveCaptureService svc)
    {
        _svc = svc;
    }

    /// <summary>
    /// POST /api/live/import - Inicia la captura de un Live dado su URL.
    /// </summary>
    [HttpPost("import")]
    public async Task<ActionResult<LiveSessionDto>> Import([FromBody] ImportLiveRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.FacebookUrl))
            return BadRequest("FacebookUrl es requerida");

        var session = await _svc.ImportAsync(req.FacebookUrl, req.Title);
        return Ok(ToDto(session, 0, 0, 0));
    }

    /// <summary>
    /// GET /api/live - Lista todas las sesiones de Live Capture.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<LiveSessionDto>>> GetAll()
    {
        var sessions = await _svc.GetAllAsync();
        var dtos = sessions.Select(s => ToDto(s, 0, 0, 0)).ToList();
        return Ok(dtos);
    }

    /// <summary>
    /// GET /api/live/{id} - Obtiene una sesión por Id.
    /// </summary>
    [HttpGet("{id:int}")]
    public async Task<ActionResult<LiveSessionDto>> GetById(int id)
    {
        var session = await _svc.GetByIdAsync(id);
        if (session == null) return NotFound();
        return Ok(ToDto(session, 0, 0, 0));
    }

    /// <summary>
    /// GET /api/live/{id}/review - Obtiene la vista completa de revisión (productos, candidatos).
    /// </summary>
    [HttpGet("{id:int}/review")]
    public async Task<ActionResult<LiveReviewDto>> GetReview(int id)
    {
        var review = await _svc.GetReviewAsync(id);
        if (review == null) return NotFound();
        return Ok(review);
    }

    /// <summary>
    /// POST /api/live/candidates/{id}/confirm - Confirma un candidato y crea una orden.
    /// </summary>
    [HttpPost("candidates/{id:int}/confirm")]
    public async Task<IActionResult> Confirm(int id, [FromBody] ConfirmCandidateRequest req)
    {
        try
        {
            await _svc.ConfirmCandidateAsync(id, req);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// POST /api/live/candidates/{id}/ignore - Ignora un candidato.
    /// </summary>
    [HttpPost("candidates/{id:int}/ignore")]
    public async Task<IActionResult> Ignore(int id)
    {
        try
        {
            await _svc.IgnoreCandidateAsync(id);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ex.Message);
        }
    }

    /// <summary>
    /// GET /api/live/candidates/{id}/clip - Devuelve un clip de 5s del audio
    /// con el momento en que se habló este pedido en la transmisión.
    /// </summary>
    [HttpGet("candidates/{id:int}/clip")]
    public async Task<IActionResult> GetClip(int id)
    {
        var (stream, contentType) = await _svc.GetCandidateClipAsync(id);
        if (stream == null || contentType == null) return NotFound();
        return File(stream, contentType, $"candidate_{id}.mp3");
    }

    private static LiveSessionDto ToDto(LiveSession s, int productCount, int candidateCount, int pendingCount) =>
        new(s.Id, s.FacebookUrl, s.Title, s.Status.ToString(), s.StatusDetail,
            s.ImportedAt, s.ProcessedAt, s.DurationSeconds,
            productCount, candidateCount, pendingCount);
}
