using System.Text.Json;
using System.Text.Json.Serialization;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/payments/webhook")]
[AllowAnonymous]
public class PaymentsWebhookController : ControllerBase
{
    private readonly IMercadoPagoOrderService _mpOrderService;

    public PaymentsWebhookController(IMercadoPagoOrderService mpOrderService)
    {
        _mpOrderService = mpOrderService;
    }

    [HttpPost]
    public async Task<IActionResult> HandleWebhook()
    {
        // Mercado Pago puede enviar el id de pago en:
        // 1) JSON body: { "type": "payment", "data": { "id": "123456" } }
        // 2) Query string: ?type=payment&data.id=123456  o  ?topic=payment&id=123456
        string? notificationType = Request.Query["type"].FirstOrDefault()
            ?? Request.Query["topic"].FirstOrDefault();
        string? paymentIdRaw = Request.Query["data.id"].FirstOrDefault()
            ?? Request.Query["id"].FirstOrDefault();

        try
        {
            using var reader = new StreamReader(Request.Body);
            var rawBody = await reader.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(rawBody))
            {
                var body = JsonSerializer.Deserialize<MpWebhookNotification>(rawBody, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (!string.IsNullOrWhiteSpace(body?.Type))
                    notificationType = body.Type;
                if (!string.IsNullOrWhiteSpace(body?.Data?.Id))
                    paymentIdRaw = body.Data.Id;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Webhook] Body no JSON o inválido: {ex.Message}");
        }

        if (!string.Equals(notificationType, "payment", StringComparison.OrdinalIgnoreCase) ||
            !long.TryParse(paymentIdRaw, out var paymentId))
        {
            // Respondemos 200 siempre para que MP no reintente eventos irrelevantes
            return Ok();
        }

        Console.WriteLine($"[Webhook] Recibida notificación de pago: {paymentId}");

        try
        {
            await _mpOrderService.ReconcileOrderPaymentAsync(paymentId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Webhook] Excepción procesando pago {paymentId}: {ex.Message}");
        }

        return Ok();
    }

    public class MpWebhookNotification
    {
        [JsonPropertyName("action")]
        public string Action { get; set; } = "";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("data")]
        public MpWebhookData? Data { get; set; }
    }

    public class MpWebhookData
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";
    }
}
