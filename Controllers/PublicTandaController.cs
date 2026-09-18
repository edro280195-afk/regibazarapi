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
    private readonly ICloudinaryService _cloudinary;
    private readonly ITandaPaymentOcrService _ocr;

    public PublicTandaController(
        ITandaService tandaService, 
        AppDbContext db, 
        IConfiguration config, 
        IHttpClientFactory httpClientFactory,
        IHubContext<DeliveryHub> hub,
        IPushNotificationService push,
        ICloudinaryService cloudinary,
        ITandaPaymentOcrService ocr)
    {
        _tandaService = tandaService;
        _db = db;
        _config = config;
        _httpClientFactory = httpClientFactory;
        _hub = hub;
        _push = push;
        _cloudinary = cloudinary;
        _ocr = ocr;
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
        var participant = await _db.TandaParticipants
            .Include(p => p.Tanda)
                .ThenInclude(t => t!.Product)
            .Include(p => p.Items)
            .Include(p => p.Client)
            .FirstOrDefaultAsync(p => p.PublicToken == token);

        Tanda? tanda = participant?.Tanda;
        if (tanda == null)
        {
            tanda = await _db.Tandas
                .Include(t => t.Participants)
                    .ThenInclude(p => p.Client)
                .Include(t => t.Participants)
                    .ThenInclude(p => p.Items)
                .FirstOrDefaultAsync(t => t.AccessToken == token);
            participant = tanda?.Participants.FirstOrDefault(p => p.Id == req.ParticipantId);
        }

        if (tanda == null) return NotFound("Tanda no encontrada.");
        if (participant == null) return NotFound("Participante no encontrado en esta tanda.");

        var mpAccessToken = _config["MercadoPago:AccessToken"]
            ?? throw new InvalidOperationException("MercadoPago:AccessToken no configurado.");

        using var httpClient = _httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {mpAccessToken}");
        httpClient.DefaultRequestHeaders.Add("X-Idempotency-Key", Guid.NewGuid().ToString());

        var paymentBody = new
        {
            transaction_amount = CalculateParticipantWeeklyAmount(tanda, participant),
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
                    AmountPaid = CalculateParticipantWeeklyAmount(tanda, participant),
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
                    Amount = CalculateParticipantWeeklyAmount(tanda, participant),
                    Type = "tanda_card_payment"
                });

                await _push.SendNotificationToAdminsAsync(
                    $"💎 Pago Tanda: ${CalculateParticipantWeeklyAmount(tanda, participant):F2}",
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

    [HttpPost("{token}/payment/proof")]
    [RequestSizeLimit(10_000_000)]
    public async Task<IActionResult> SubmitPaymentProof(
        string token,
        [FromForm] int weekNumber,
        [FromForm] IFormFile proof,
        CancellationToken cancellationToken)
    {
        if (weekNumber < 1)
            return BadRequest(new { message = "La semana del comprobante no es válida." });

        if (proof == null || proof.Length == 0)
            return BadRequest(new { message = "Selecciona una imagen del comprobante." });

        if (!proof.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "El comprobante debe ser una imagen." });

        var participant = await _db.TandaParticipants
            .Include(p => p.Tanda)
            .Include(p => p.Payments)
            .FirstOrDefaultAsync(p => p.PublicToken == token, cancellationToken);

        if (participant?.Tanda == null)
            return BadRequest(new { message = "Usa el enlace individual de tu tanda para enviar el comprobante." });

        if (participant.Payments.Any(p => p.WeekNumber == weekNumber && p.IsVerified))
            return BadRequest(new { message = "Esta semana ya tiene un pago verificado." });

        await using var image = new MemoryStream();
        await proof.CopyToAsync(image, cancellationToken);
        image.Position = 0;
        var ocr = await _ocr.ExtractAsync(image, cancellationToken);
        image.Position = 0;
        var proofUrl = await _cloudinary.UploadAsync(image, proof.FileName, "tanda-payment-proofs");

        var pendingPayment = participant.Payments
            .FirstOrDefault(p => p.WeekNumber == weekNumber && !p.IsVerified);
        var payment = pendingPayment ?? new TandaPayment
        {
            Id = Guid.NewGuid(),
            ParticipantId = participant.Id,
            WeekNumber = weekNumber,
            PaymentDate = DateTime.UtcNow,
            IsVerified = false
        };

        payment.AmountPaid = ocr.Amount ?? 0;
        payment.OcrAmount = ocr.Amount;
        payment.DepositDate = ocr.DepositDate;
        payment.ProofUrl = proofUrl;
        payment.OcrText = ocr.Text;
        payment.OcrConfidence = ocr.Confidence;
        payment.PaymentMethod = "Transferencia/Depósito";
        payment.Notes = string.IsNullOrWhiteSpace(ocr.Error)
            ? "Comprobante enviado por la clienta; pendiente de verificación."
            : $"Comprobante enviado; OCR no disponible: {ocr.Error}";

        if (pendingPayment == null)
            _db.TandaPayments.Add(payment);

        await _db.SaveChangesAsync(cancellationToken);

        await _hub.Clients.Group("Admins").SendAsync("DeliveryUpdate", new
        {
            TandaId = participant.TandaId,
            ParticipantId = participant.Id,
            WeekNumber = weekNumber,
            Amount = ocr.Amount,
            Type = "tanda_payment_proof"
        }, cancellationToken);

        return Ok(new
        {
            message = "Comprobante recibido. Administración revisará los datos extraídos.",
            paymentId = payment.Id,
            amount = ocr.Amount,
            depositDate = ocr.DepositDate,
            confidence = ocr.Confidence,
            proofUrl
        });
    }

    public class TandaCardPaymentRequest
    {
        public Guid ParticipantId { get; set; }
        public int WeekNumber { get; set; }
        public string CardToken { get; set; } = "";
        public string PaymentMethodId { get; set; } = "";
    }

    private static decimal CalculateParticipantWeeklyAmount(Tanda tanda, TandaParticipant participant)
    {
        if (participant.WeeklyAmount.HasValue)
            return participant.WeeklyAmount.Value;

        var itemAmount = participant.Items
            .Where(item => item.WeeklyAmount.HasValue)
            .Sum(item => item.WeeklyAmount!.Value * item.Quantity);
        return itemAmount > 0 ? itemAmount : tanda.WeeklyAmount;
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
