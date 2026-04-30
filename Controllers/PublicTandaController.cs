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

    [HttpPost("{token}/payment/card")]
    public async Task<IActionResult> PayWithCard(string token, [FromBody] TandaCardPaymentRequest req)
    {
        var tanda = await _db.Tandas
            .Include(t => t.Participants)
                .ThenInclude(p => p.Client)
            .FirstOrDefaultAsync(t => t.AccessToken == token);

        if (tanda == null) return NotFound("Tanda no encontrada.");

        var participant = tanda.Participants.FirstOrDefault(p => p.Id == req.ParticipantId);
        if (participant == null) return NotFound("Participante no encontrado en esta tanda.");

        var mpAccessToken = _config["MercadoPago:AccessToken"]
            ?? throw new InvalidOperationException("MercadoPago:AccessToken no configurado.");

        using var httpClient = _httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {mpAccessToken}");
        httpClient.DefaultRequestHeaders.Add("X-Idempotency-Key", Guid.NewGuid().ToString());

        var paymentBody = new
        {
            transaction_amount = tanda.WeeklyAmount,
            token = req.CardToken,
            description = $"Tanda {tanda.Name} - Semana {req.WeekNumber}",
            installments = 1,
            payment_method_id = req.PaymentMethodId,
            payer = new { email = "pagos@regibazar.com" },
            external_reference = $"tanda_{tanda.Id}_{participant.Id}_{req.WeekNumber}",
            metadata = new { 
                tanda_id = tanda.Id, 
                participant_id = participant.Id, 
                week = req.WeekNumber,
                type = "tanda_payment" 
            }
        };

        try
        {
            var response = await httpClient.PostAsJsonAsync("https://api.mercadopago.com/v1/payments", paymentBody);
            var rawBody = await response.Content.ReadAsStringAsync();
            
            if (!response.IsSuccessStatusCode)
            {
                return StatusCode((int)response.StatusCode, new { message = "Error en Mercado Pago", details = rawBody });
            }

            var mpResult = System.Text.Json.JsonSerializer.Deserialize<MpPaymentApiResponse>(rawBody, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            
            if (mpResult?.Status == "approved")
            {
                // Registrar el pago en la Tanda
                var payment = new TandaPayment
                {
                    ParticipantId = participant.Id,
                    WeekNumber = req.WeekNumber,
                    AmountPaid = tanda.WeeklyAmount,
                    PaymentDate = DateTime.UtcNow,
                    IsVerified = true,
                    Notes = $"MP#{mpResult.Id} (Tarjeta)"
                };
                _db.TandaPayments.Add(payment);
                await _db.SaveChangesAsync();

                // Notificar a Admins
                var clientName = participant.Client?.Name ?? "Participante";
                await _hub.Clients.Group("Admins").SendAsync("DeliveryUpdate", new {
                    TandaId = tanda.Id,
                    ParticipantName = clientName,
                    Amount = tanda.WeeklyAmount,
                    Type = "tanda_card_payment"
                });

                await _push.SendNotificationToAdminsAsync(
                    $"💎 Pago Tanda: ${tanda.WeeklyAmount:F2}",
                    $"{clientName} pagó la semana {req.WeekNumber} de {tanda.Name}.",
                    tag: "tanda-payment"
                );
            }

            return Ok(new { 
                status = mpResult?.Status, 
                statusDetail = mpResult?.StatusDetail, 
                paymentId = mpResult?.Id 
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    public class TandaCardPaymentRequest
    {
        public Guid ParticipantId { get; set; }
        public int WeekNumber { get; set; }
        public string CardToken { get; set; } = "";
        public string PaymentMethodId { get; set; } = "";
    }

    private class MpPaymentApiResponse
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }
        [JsonPropertyName("status")]
        public string Status { get; set; } = "";
        [JsonPropertyName("status_detail")]
        public string StatusDetail { get; set; } = "";
    }
}
