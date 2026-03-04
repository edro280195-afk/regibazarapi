using EntregasApi.DTOs;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SalesPeriodsController : ControllerBase
{
    private readonly ISalesPeriodService _service;

    public SalesPeriodsController(ISalesPeriodService service) => _service = service;

    /// <summary>GET api/salesperiods — Lista todos los cortes</summary>
    [HttpGet]
    public async Task<ActionResult<List<SalesPeriodDto>>> GetAll()
    {
        var list = await _service.GetAllAsync();
        return Ok(list);
    }

    /// <summary>POST api/salesperiods — Crea un nuevo corte</summary>
    [HttpPost]
    public async Task<ActionResult<SalesPeriodDto>> Create([FromBody] CreateSalesPeriodRequest request)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var created = await _service.CreateAsync(request);
        return CreatedAtAction(nameof(GetAll), new { id = created.Id }, created);
    }

    /// <summary>PATCH api/salesperiods/{id}/activate — Activa este corte y apaga los demás</summary>
    [HttpPatch("{id}/activate")]
    public async Task<IActionResult> Activate(int id)
    {
        var period = await _service.ActivateAsync(id);
        if (period == null) return NotFound();
        return Ok(period);
    }

    [HttpPost("{id}/sync")]
    public async Task<IActionResult> Sync(int id, [FromBody] SyncSalesPeriodRequest request)
    {
        var count = await _service.SyncRelatedEntitiesAsync(id, request);
        return Ok(new { Count = count });
    }
}
