using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EntregasApi.DTOs;
using EntregasApi.Services;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/public-tanda")]
[AllowAnonymous] 
public class PublicTandaController : ControllerBase
{
    private readonly ITandaService _tandaService;

    public PublicTandaController(ITandaService tandaService)
    {
        _tandaService = tandaService;
    }

    [HttpGet("{token}")]
    public async Task<IActionResult> GetTandaByToken(string token)
    {
        try
        {
            var tanda = await _tandaService.GetTandaByTokenAsync(token);
            
            if (tanda == null)
            {
                return NotFound(new { message = "Tanda no encontrada o enlace inválido." });
            }

            return Ok(tanda);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
