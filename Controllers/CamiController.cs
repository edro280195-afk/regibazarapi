using EntregasApi.DTOs;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CamiController : ControllerBase
{
    private readonly ICamiService _cami;
    private readonly IGoogleTtsService _tts;
    private readonly ILogger<CamiController> _logger;

    public CamiController(ICamiService cami, IGoogleTtsService tts, ILogger<CamiController> logger)
    {
        _cami = cami;
        _tts = tts;
        _logger = logger;
    }

    /// <summary>
    /// Endpoint conversacional de C.A.M.I.
    /// Recibe el historial completo + nuevo mensaje, devuelve la respuesta de CAMI.
    /// CAMI puede consultar y operar el sistema ERP completo via function calling.
    /// </summary>
    [HttpPost("chat")]
    public async Task<ActionResult<CamiChatResponse>> Chat([FromBody] CamiChatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.NewMessage))
            return BadRequest("El mensaje no puede estar vacío.");

        try
        {
            var text = await _cami.ChatAsync(request);
            
            // Sintetizamos la voz de CAMI usando el nuevo servicio
            string? audioBase64 = null;
            try 
            {
                audioBase64 = await _tts.SynthesizeAsync(text);
            }
            catch (Exception ttsEx)
            {
                _logger.LogWarning(ttsEx, "No se pudo sintetizar la voz de CAMI, pero se enviará el texto.");
            }

            return Ok(new CamiChatResponse(text, audioBase64));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en POST /api/cami/chat");
            return StatusCode(500, new CamiChatResponse(
                "Lo siento, tuve un problema técnico. ¿Lo intentamos de nuevo?"));
        }
    }
}
