using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EntregasApi.DTOs;
using EntregasApi.Services;
using EntregasApi.Data;
using Microsoft.AspNetCore.SignalR;
using EntregasApi.Hubs;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using EntregasApi.Models;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/public-tanda")]
[AllowAnonymous] 
public class PublicTandaController : ControllerBase
{
    private readonly ITandaService _tandaService;
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHubContext<DeliveryHub> _hub;
    private readonly IPushNotificationService _push;

    public PublicTandaController(
        ITandaService tandaService, 
        AppDbContext db, 
        IConfiguration config, 
        IHttpClientFactory httpClientFactory,
        IHubContext<DeliveryHub> hub,
        IPushNotificationService push)
    {
        _tandaService = tandaService;
        _db = db;
        _config = config;
        _httpClientFactory = httpClientFactory;
        _hub = hub;
        _push = push;
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

    [HttpPost("{token}/payment-proofs")]
    [RequestSizeLimit(8_000_000)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadPaymentProof(
        string token,
        [FromForm] int weekNumber,
        [FromForm] decimal? amountClaimed,
        [FromForm] IFormFile? file,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { message = "Selecciona la foto de tu comprobante." });
        if (file.Length > 8_000_000)
            return BadRequest(new { message = "La imagen no puede superar 8 MB." });

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await _tandaService.UploadPaymentProofAsync(
                token,
                weekNumber,
                amountClaimed ?? 0,
                stream,
                file.FileName,
                file.ContentType,
                cancellationToken);

            await _hub.Clients.Group("Admins").SendAsync("TandaPaymentProofSubmitted", new
            {
                result.Proof.Id,
                result.TandaId,
                result.ParticipantName,
                result.Proof.WeekNumber,
                result.Proof.AmountClaimed
            }, cancellationToken);

            await _push.SendNotificationToAdminsAsync(
                "💖 Nuevo comprobante de tanda",
                $"{result.ParticipantName} envió su comprobante de la semana {result.Proof.WeekNumber}.",
                tag: "tanda-payment-proof");

            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception)
        {
            return StatusCode(500, new { message = "No se pudo recibir el comprobante." });
        }
    }
}
