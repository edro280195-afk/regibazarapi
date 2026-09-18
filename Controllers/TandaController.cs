using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EntregasApi.DTOs;
using EntregasApi.Services;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class TandaController : ControllerBase
{
    private readonly ITandaService _tandaService;
    private readonly IRaffleService _raffleService;

    public TandaController(ITandaService tandaService, IRaffleService raffleService)
    {
        _tandaService = tandaService;
        _raffleService = raffleService;
    }

    [HttpGet]
    public async Task<IActionResult> GetTandas(CancellationToken cancellationToken)
    {
        try
        {
            var tandas = await _tandaService.GetTandasAsync(cancellationToken);
            return Ok(tandas);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetTanda(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var tanda = await _tandaService.GetTandaByIdAsync(id, cancellationToken);
            if (tanda == null) return NotFound(new { message = "Tanda no encontrada" });
            return Ok(tanda);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> CreateTanda(
        [FromBody] CreateTandaDto dto,
        CancellationToken cancellationToken)
    {
        try
        {
            var tanda = await _tandaService.CreateTandaAsync(dto, cancellationToken);
            return Ok(tanda); // Retorna 200 con la configuración inicial
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("participants")]
    public async Task<IActionResult> AddParticipant(
        [FromBody] AddParticipantDto dto,
        CancellationToken cancellationToken)
    {
        try
        {
            var participant = await _tandaService.AddParticipantAsync(dto, cancellationToken);
            return Ok(participant); // Retorna la inscripción exitosa
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("payments")]
    public async Task<IActionResult> RegisterPayment(
        [FromBody] RegisterPaymentDto dto,
        CancellationToken cancellationToken)
    {
        try
        {
            var payment = await _tandaService.RegisterPaymentAsync(dto, cancellationToken);
            return Ok(payment); // Retorna el abono registrado
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message }); // Excepción controlada de regla de negocio
        }
    }

    [HttpPatch("payments/{id}/verification")]
    public async Task<IActionResult> VerifyPayment(Guid id, [FromBody] VerifyTandaPaymentDto dto)
    {
        try
        {
            return Ok(await _tandaService.VerifyPaymentAsync(id, dto));
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("{id}/sunday-delivery")]
    public async Task<IActionResult> GetSundayDelivery(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var participant = await _tandaService.GetSundayDeliveryAsync(id, cancellationToken);
            
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

    [HttpGet("{id}/payment-proofs")]
    public async Task<IActionResult> GetPaymentProofs(
        Guid id,
        [FromQuery] string? status,
        CancellationToken cancellationToken)
    {
        try
        {
            var proofs = await _tandaService.GetPaymentProofsAsync(id, status, cancellationToken);
            return Ok(proofs);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("payment-proofs/{id}/review")]
    public async Task<IActionResult> ReviewPaymentProof(
        Guid id,
        [FromBody] ReviewTandaPaymentProofDto dto,
        CancellationToken cancellationToken)
    {
        try
        {
            var reviewer = User.Identity?.Name
                ?? User.FindFirst("sub")?.Value
                ?? "Admin";
            var proof = await _tandaService.ReviewPaymentProofAsync(id, dto, reviewer, cancellationToken);
            return Ok(proof);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id}/process-penalties")]
    public async Task<IActionResult> ProcessPenalties(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await _tandaService.ProcessPenaltiesAsync(id, cancellationToken);
            return Ok(new { message = "Corte Dominical: Penalizaciones procesadas correctamente." });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id}/shuffle-with-raffle")]
    public async Task<IActionResult> ShuffleWithRaffle(Guid id, [FromBody] SelectWinnerDto dto)
    {
        try
        {
            // Buscar o crear sorteo vinculado a esta tanda
            var existingRaffles = await _raffleService.GetRafflesByTandaAsync(id);
            var activeRaffle = existingRaffles.FirstOrDefault(r => r.Status == "Draft" || r.Status == "Active");

            if (activeRaffle == null)
            {
                // Crear sorteo automáticamente para esta tanda
                var tanda = await _tandaService.GetTandaByIdAsync(id);
                if (tanda == null)
                    return NotFound(new { message = "Tanda no encontrada" });

                var createDto = new CreateRaffleDto
                {
                    Name = $"Sorteo de turnos - {tanda.Name}",
                    Description = "Sorteo de asignación de turnos para la tanda",
                    AnimationType = "roulette",
                    PrizeType = "custom",
                    PrizeDescription = "Nuevo turno asignado",
                    RequiredPurchases = 1,
                    EligibilityRule = "purchaseCount",
                    ClientSegmentFilter = "all",
                    TandaId = id,
                    ShuffleTandaTurns = true,
                    RaffleDate = DateTime.UtcNow,
                    NotifyWinner = false,
                    AutoDraw = false
                };

                var raffle = await _raffleService.CreateRaffleAsync(createDto);
                await _raffleService.UpdateRaffleAsync(raffle.Id, new UpdateRaffleDto { Status = "Active" });

                // Evaluar participantes de la tanda
                await _raffleService.EvaluateRaffleAsync(raffle.Id);

                // Hacer el shuffle
                var result = await _raffleService.ShuffleTandaTurnsAsync(raffle.Id, dto);

                return Ok(result);
            }

            // Si ya existe un sorteo activo, usarlo
            await _raffleService.EvaluateRaffleAsync(activeRaffle.Id);
            var shuffleResult = await _raffleService.ShuffleTandaTurnsAsync(activeRaffle.Id, dto);

            return Ok(shuffleResult);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("{id}/raffles")]
    public async Task<IActionResult> GetTandaRaffles(Guid id)
    {
        try
        {
            var raffles = await _raffleService.GetRafflesByTandaAsync(id);
            return Ok(raffles);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("products")]
    public async Task<IActionResult> GetProducts(CancellationToken cancellationToken)
    {
        try
        {
            var products = await _tandaService.GetProductsAsync(cancellationToken);
            return Ok(products);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("products")]
    public async Task<IActionResult> CreateProduct(
        [FromBody] CreateTandaProductDto dto,
        CancellationToken cancellationToken)
    {
        try
        {
            var product = await _tandaService.CreateProductAsync(dto.Name, dto.BasePrice, cancellationToken);
            return Ok(product);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateTanda(
        Guid id,
        [FromBody] UpdateTandaDto dto,
        CancellationToken cancellationToken)
    {
        try
        {
            var tanda = await _tandaService.UpdateTandaAsync(id, dto, cancellationToken);
            return Ok(tanda);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPatch("participants/{id}/turn")]
    public async Task<IActionResult> UpdateParticipantTurn(
        Guid id,
        [FromBody] UpdateTurnDto dto,
        CancellationToken cancellationToken)
    {
        try
        {
            await _tandaService.UpdateParticipantTurnAsync(id, dto.NewTurn, cancellationToken);
            return Ok(new { message = "Turno actualizado correctamente" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPatch("participants/{id}/variant")]
    public async Task<IActionResult> UpdateParticipantVariant(
        Guid id,
        [FromBody] UpdateParticipantVariantDto dto,
        CancellationToken cancellationToken)
    {
        try
        {
            await _tandaService.UpdateParticipantVariantAsync(id, dto.Variant, cancellationToken);
            return Ok(new { message = "Variante actualizada correctamente" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("participants/{id}/items")]
    public async Task<IActionResult> ReplaceParticipantItems(Guid id, [FromBody] ReplaceTandaParticipantItemsDto dto)
    {
        try
        {
            await _tandaService.ReplaceParticipantItemsAsync(id, dto);
            return Ok(new { message = "Artículos actualizados correctamente" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPatch("participants/{id}/confirm-delivery")]
    public async Task<IActionResult> ConfirmParticipantDelivery(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await _tandaService.ConfirmParticipantDeliveryAsync(id, cancellationToken);
            return Ok(new { message = "¡Entrega de tanda confirmada! ✨" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("participants/{id}")]
    public async Task<IActionResult> RemoveParticipant(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await _tandaService.RemoveParticipantAsync(id, cancellationToken);
            return Ok(new { message = "Participante eliminado correctamente" });
        }
        catch (Exception ex)
        {
            var message = ex.InnerException?.Message ?? ex.Message;
            return BadRequest(new { message });
        }
    }

    [HttpDelete("payments/{id}")]
    public async Task<IActionResult> DeletePayment(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await _tandaService.DeletePaymentAsync(id, cancellationToken);
            return Ok(new { message = "Pago eliminado correctamente" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id}/reorder")]
    public async Task<IActionResult> ReorderParticipants(
        Guid id,
        [FromBody] ReorderParticipantsDto dto,
        CancellationToken cancellationToken)
    {
        try
        {
            await _tandaService.ReorderParticipantsAsync(id, dto.ParticipantIds, cancellationToken);
            return Ok(new { message = "Orden de participantes actualizado correctamente ✨" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("participants/{id}")]
    public async Task<IActionResult> UpdateParticipant(
        Guid id,
        [FromBody] UpdateTandaParticipantDto dto,
        CancellationToken cancellationToken)
    {
        try
        {
            var participant = await _tandaService.UpdateParticipantAsync(id, dto, cancellationToken);
            return Ok(participant);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("payments/{id}")]
    public async Task<IActionResult> UpdatePayment(
        Guid id,
        [FromBody] UpdateTandaPaymentDto dto,
        CancellationToken cancellationToken)
    {
        try
        {
            var payment = await _tandaService.UpdatePaymentAsync(id, dto, cancellationToken);
            return Ok(payment);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{id}/places")]
    public async Task<IActionResult> UpdatePlaces(
        Guid id,
        [FromBody] UpdateTandaPlacesDto dto,
        CancellationToken cancellationToken)
    {
        try
        {
            await _tandaService.UpdatePlacesAsync(id, dto.Assignments, cancellationToken);
            return Ok(new { message = "Lugares actualizados correctamente" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
