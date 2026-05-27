using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/clients")]
[Authorize]
public class ClientResolutionController : ControllerBase
{
    private readonly IClientResolverService _resolver;
    private readonly AppDbContext _db;

    public ClientResolutionController(IClientResolverService resolver, AppDbContext db)
    {
        _resolver = resolver;
        _db = db;
    }

    /// <summary>
    /// POST /api/clients/resolve - Devuelve candidatas a clienta con score y acción sugerida.
    /// </summary>
    [HttpPost("resolve")]
    public async Task<ActionResult<ResolveClientResponse>> Resolve([FromBody] ResolveClientRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name) &&
            string.IsNullOrWhiteSpace(req.Phone) &&
            string.IsNullOrWhiteSpace(req.Address))
        {
            return Ok(new ResolveClientResponse(new List<ResolveCandidateDto>(), "create"));
        }
        var result = await _resolver.ResolveAsync(req.Name ?? string.Empty, req.Phone, req.Address);
        return Ok(result);
    }

    /// <summary>
    /// POST /api/clients/{id}/aliases - Agrega un alias a la clienta. Idempotente:
    /// si ya existe el mismo alias normalizado, incrementa TimesSeen.
    /// </summary>
    [HttpPost("{id}/aliases")]
    public async Task<ActionResult<ClientAliasDto>> AddAlias(int id, [FromBody] AddAliasRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Alias)) return BadRequest("Alias vacío");

        var source = ClientAliasSource.ManualConfirm;
        if (!string.IsNullOrWhiteSpace(req.Source) && Enum.TryParse<ClientAliasSource>(req.Source, true, out var parsed))
        {
            source = parsed;
        }

        try
        {
            var dto = await _resolver.AddAliasAsync(id, req.Alias, source);
            return Ok(dto);
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
    /// GET /api/clients/{id}/aliases - Lista los alias conocidos de una clienta.
    /// </summary>
    [HttpGet("{id}/aliases")]
    public async Task<ActionResult<List<ClientAliasDto>>> GetAliases(int id)
    {
        var aliases = await _db.ClientAliases
            .Where(a => a.ClientId == id)
            .OrderByDescending(a => a.TimesSeen)
            .ThenByDescending(a => a.CreatedAt)
            .Select(a => new ClientAliasDto(a.Id, a.Alias, a.Source.ToString(), a.TimesSeen, a.CreatedAt))
            .ToListAsync();
        return Ok(aliases);
    }

    /// <summary>
    /// DELETE /api/clients/aliases/{aliasId} - Borra un alias específico.
    /// </summary>
    [HttpDelete("aliases/{aliasId}")]
    public async Task<IActionResult> DeleteAlias(int aliasId)
    {
        var alias = await _db.ClientAliases.FirstOrDefaultAsync(a => a.Id == aliasId);
        if (alias == null) return NotFound();
        _db.ClientAliases.Remove(alias);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// POST /api/clients/merge - Fusiona la clienta source en target. Reasigna órdenes,
    /// transacciones de puntos y aliases. Borra el source.
    /// </summary>
    [HttpPost("merge")]
    public async Task<IActionResult> Merge([FromBody] MergeClientsRequest req)
    {
        if (req.SourceId == req.TargetId) return BadRequest("Source y target son la misma clienta");

        try
        {
            await _resolver.MergeAsync(req.SourceId, req.TargetId);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ex.Message);
        }
    }

    /// <summary>
    /// GET /api/clients/duplicate-suggestions - Pares de clientas candidatas a ser duplicados,
    /// por teléfono igual o nombre/dirección parecidos.
    /// </summary>
    [HttpGet("duplicate-suggestions")]
    public async Task<ActionResult<List<DuplicateSuggestionDto>>> DuplicateSuggestions([FromQuery] int limit = 50)
    {
        var capped = Math.Clamp(limit, 1, 200);
        var suggestions = await _resolver.GetDuplicateSuggestionsAsync(capped);
        return Ok(suggestions);
    }
}
