using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EntregasApi.DTOs;
using EntregasApi.Services;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize] // Protegemos el endpoint por defecto
[Authorize]
public class TandaController : ControllerBase
{
    private readonly ITandaService _tandaService;

    public TandaController(ITandaService tandaService)
    {
        _tandaService = tandaService;
    }

    [HttpGet]
    public async Task<IActionResult> GetTandas()
    {
        try
        {
            var tandas = await _tandaService.GetTandasAsync();
            return Ok(tandas);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetTanda(Guid id)
    {
        try
        {
            var tanda = await _tandaService.GetTandaByIdAsync(id);
            if (tanda == null) return NotFound(new { message = "Tanda no encontrada" });
            return Ok(tanda);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> CreateTanda([FromBody] CreateTandaDto dto)
    {
        try
        {
            var tanda = await _tandaService.CreateTandaAsync(dto);
            return Ok(tanda); // Retorna 200 con la configuración inicial
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("participants")]
    public async Task<IActionResult> AddParticipant([FromBody] AddParticipantDto dto)
    {
        try
        {
            var participant = await _tandaService.AddParticipantAsync(dto);
            return Ok(participant); // Retorna la inscripción exitosa
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("payments")]
    public async Task<IActionResult> RegisterPayment([FromBody] RegisterPaymentDto dto)
    {
        try
        {
            var payment = await _tandaService.RegisterPaymentAsync(dto);
            return Ok(payment); // Retorna el abono registrado
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message }); // Excepción controlada de regla de negocio
        }
    }

    [HttpGet("{id}/sunday-delivery")]
    public async Task<IActionResult> GetSundayDelivery(Guid id)
    {
        try
        {
            var participant = await _tandaService.GetSundayDeliveryAsync(id);
            
            if (participant == null)
            {
                return NotFound(new { message = "Nadie tomó el turno para recibir el producto esta semana." });
            }
            return Ok(participant);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id}/process-penalties")]
    public async Task<IActionResult> ProcessPenalties(Guid id)
    {
        try
        {
            await _tandaService.ProcessPenaltiesAsync(id);
            return Ok(new { message = "Corte Dominical: Penalizaciones procesadas correctamente." });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id}/shuffle")]
    public async Task<IActionResult> ShuffleParticipants(Guid id)
    {
        try
        {
            await _tandaService.ShuffleParticipantsAsync(id);
            return Ok(new { message = "Sorteo realizado con éxito 🎲" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("products")]
    public async Task<IActionResult> GetProducts()
    {
        try
        {
            var products = await _tandaService.GetProductsAsync();
            return Ok(products);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("products")]
    public async Task<IActionResult> CreateProduct([FromBody] CreateTandaProductDto dto)
    {
        try
        {
            var product = await _tandaService.CreateProductAsync(dto.Name, dto.BasePrice);
            return Ok(product);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateTanda(Guid id, [FromBody] UpdateTandaDto dto)
    {
        try
        {
            var tanda = await _tandaService.UpdateTandaAsync(id, dto);
            return Ok(tanda);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPatch("participants/{id}/turn")]
    public async Task<IActionResult> UpdateParticipantTurn(Guid id, [FromBody] UpdateTurnDto dto)
    {
        try
        {
            await _tandaService.UpdateParticipantTurnAsync(id, dto.NewTurn);
            return Ok(new { message = "Turno actualizado correctamente" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPatch("participants/{id}/variant")]
    public async Task<IActionResult> UpdateParticipantVariant(Guid id, [FromBody] UpdateParticipantVariantDto dto)
    {
        try
        {
            await _tandaService.UpdateParticipantVariantAsync(id, dto.Variant);
            return Ok(new { message = "Variante actualizada correctamente" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("participants/{id}")]
    public async Task<IActionResult> RemoveParticipant(Guid id)
    {
        try
        {
            await _tandaService.RemoveParticipantAsync(id);
            return Ok(new { message = "Participante eliminado correctamente" });
        }
        catch (Exception ex)
        {
            var message = ex.InnerException?.Message ?? ex.Message;
            return BadRequest(new { message });
        }
    }
}
