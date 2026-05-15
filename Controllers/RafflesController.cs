using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EntregasApi.DTOs;
using EntregasApi.Services;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class RafflesController : ControllerBase
{
    private readonly IRaffleService _raffleService;

    public RafflesController(IRaffleService raffleService)
    {
        _raffleService = raffleService;
    }

    [HttpGet]
    public async Task<IActionResult> GetRaffles([FromQuery] string? status = null)
    {
        try
        {
            var raffles = await _raffleService.GetRafflesAsync(status);
            return Ok(raffles);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("active")]
    public async Task<IActionResult> GetActiveRaffles()
    {
        try
        {
            var raffles = await _raffleService.GetActiveRafflesAsync();
            return Ok(raffles);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetRaffleHistory()
    {
        try
        {
            var raffles = await _raffleService.GetRaffleHistoryAsync();
            return Ok(raffles);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("by-tanda/{tandaId}")]
    public async Task<IActionResult> GetRafflesByTanda(Guid tandaId)
    {
        try
        {
            var raffles = await _raffleService.GetRafflesByTandaAsync(tandaId);
            return Ok(raffles);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetRaffle(Guid id)
    {
        try
        {
            var raffle = await _raffleService.GetRaffleByIdAsync(id);
            return Ok(raffle);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> CreateRaffle([FromBody] CreateRaffleDto dto)
    {
        try
        {
            var raffle = await _raffleService.CreateRaffleAsync(dto);
            return CreatedAtAction(nameof(GetRaffle), new { id = raffle.Id }, raffle);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateRaffle(Guid id, [FromBody] UpdateRaffleDto dto)
    {
        try
        {
            var raffle = await _raffleService.UpdateRaffleAsync(id, dto);
            return Ok(raffle);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteRaffle(Guid id)
    {
        try
        {
            await _raffleService.DeleteRaffleAsync(id);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id}/evaluate")]
    public async Task<IActionResult> EvaluateRaffle(Guid id)
    {
        try
        {
            var result = await _raffleService.EvaluateRaffleAsync(id);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id}/select-winner")]
    public async Task<IActionResult> SelectWinner(Guid id, [FromBody] SelectWinnerDto dto)
    {
        try
        {
            var draws = await _raffleService.SelectWinnerAsync(id, dto);
            return Ok(draws);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id}/shuffle-tanda-turns")]
    public async Task<IActionResult> ShuffleTandaTurns(Guid id, [FromBody] SelectWinnerDto dto)
    {
        try
        {
            var result = await _raffleService.ShuffleTandaTurnsAsync(id, dto);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id}/announce")]
    public async Task<IActionResult> AnnounceWinner(Guid id)
    {
        try
        {
            var raffle = await _raffleService.AnnounceWinnerAsync(id);
            return Ok(raffle);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
