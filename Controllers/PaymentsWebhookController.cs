using EntregasApi.Data;
using EntregasApi.Hubs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/payments/webhook")]
[AllowAnonymous]
public class PaymentsWebhookController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IHubContext<DeliveryHub> _hub;
    private readonly IPushNotificationService _push;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;

    public PaymentsWebhookController(
        AppDbContext db,
        IHubContext<DeliveryHub> hub,
        IPushNotificationService push,
        IHttpClientFactory httpClientFactory,
        IConfiguration config)
    {
        _db = db;
        _hub = hub;
        _push = push;
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    [HttpPost]
    public async Task<IActionResult> HandleWebhook([FromBody] MpWebhookNotification notification)
    {
        // Mercado Pago envía notificaciones de varios tipos. Solo nos interesa 'payment'.
        if (notification.Type != "payment" || notification.Data?.Id == null)
        {
            return Ok(); // Respondemos 200 siempre para que MP deje de reintentar cosas irrelevantes
        }

        var paymentId = notification.Data.Id;
        Console.WriteLine($"[Webhook] Recibida notificación de pago: {paymentId}");

        var mpAccessToken = _config["MercadoPago:AccessToken"];
        if (string.IsNullOrEmpty(mpAccessToken))
        {
            Console.WriteLine("[Webhook] Error: MercadoPago:AccessToken no configurado.");
            return Ok();
        }

        // Consultar el estado real del pago a la API de Mercado Pago
        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {mpAccessToken}");

        try
        {
            var response = await client.GetAsync($"https://api.mercadopago.com/v1/payments/{paymentId}");
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[Webhook] Error al consultar pago {paymentId}: {response.StatusCode}");
                return Ok();
            }

            var rawBody = await response.Content.ReadAsStringAsync();
            var mpPayment = System.Text.Json.JsonSerializer.Deserialize<MpPaymentDetail>(rawBody, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (mpPayment == null || string.IsNullOrEmpty(mpPayment.ExternalReference))
            {
                Console.WriteLine($"[Webhook] Pago {paymentId} no tiene external_reference.");
                return Ok();
            }

            // Procesar según el tipo de referencia
            if (mpPayment.ExternalReference.StartsWith("order_"))
            {
                await ProcessOrderPayment(mpPayment);
            }
            // Aquí podríamos agregar tanda_ si fuera necesario en el futuro
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Webhook] Excepción procesando pago {paymentId}: {ex.Message}");
        }

        return Ok();
    }

    private async Task ProcessOrderPayment(MpPaymentDetail mpPayment)
    {
        if (!int.TryParse(mpPayment.ExternalReference.Replace("order_", ""), out int orderId))
            return;

        var order = await _db.Orders
            .Include(o => o.Payments)
            .Include(o => o.Client)
            .FirstOrDefaultAsync(o => o.Id == orderId);

        if (order == null)
        {
            Console.WriteLine($"[Webhook] Pedido #{orderId} no encontrado.");
            return;
        }

        // Verificar si ya registramos este ID de pago
        var existingPayment = order.Payments?.FirstOrDefault(p => p.Notes != null && p.Notes.Contains($"MP#{mpPayment.Id}"));

        if (mpPayment.Status == "approved")
        {
            if (existingPayment == null)
            {
                // Es un pago nuevo aprobado (quizás la clienta cerró el navegador antes)
                var payment = new OrderPayment
                {
                    OrderId = order.Id,
                    Amount = mpPayment.TransactionAmount,
                    Method = "Tarjeta",
                    RegisteredBy = "Sistema (Webhook)",
                    Notes = $"MP#{mpPayment.Id} | {mpPayment.StatusDetail} (Auto)"
                };
                _db.OrderPayments.Add(payment);
                
                // Confirmar pedido si estaba pendiente
                if (order.Status is Models.OrderStatus.Pending or Models.OrderStatus.Postponed)
                {
                    order.Status = Models.OrderStatus.Confirmed;
                }

                await _db.SaveChangesAsync();

                // Notificar a Admins
                await NotifyAdmins(order, mpPayment.TransactionAmount, "aprobado");
            }
            else if (existingPayment.Notes != null && !existingPayment.Notes.Contains("approved"))
            {
                // El pago existía (ej. estaba 'in_process') y ahora se aprobó
                existingPayment.Notes = $"MP#{mpPayment.Id} | approved (Webhook)";
                
                if (order.Status is Models.OrderStatus.Pending or Models.OrderStatus.Postponed)
                {
                    order.Status = Models.OrderStatus.Confirmed;
                }

                await _db.SaveChangesAsync();
                await NotifyAdmins(order, mpPayment.TransactionAmount, "confirmado (era pendiente)");
            }
        }
        else if (mpPayment.Status == "rejected")
        {
            if (existingPayment != null)
            {
                existingPayment.Notes = $"MP#{mpPayment.Id} | rejected (Webhook)";
                await _db.SaveChangesAsync();
                // Opcional: Notificar rechazo
            }
        }
    }

    private async Task NotifyAdmins(Order order, decimal amount, string action)
    {
        var clientName = order.Client?.Name ?? "Clienta";
        
        // SignalR
        await _hub.Clients.Group("Admins").SendAsync("DeliveryUpdate", new
        {
            OrderId = order.Id,
            Status = order.Status.ToString(),
            ChangeType = "card_payment_webhook",
            ClientName = clientName,
            AmountPaid = amount
        });

        // Push
        await _push.SendNotificationToAdminsAsync(
            $"💳 Pago {action}: ${amount:F2}",
            $"Pedido #{order.Id} de {clientName} actualizado vía Mercado Pago.",
            tag: "card-payment-webhook"
        );
    }

    // DTOs para el Webhook
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

    public class MpPaymentDetail
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("status_detail")]
        public string StatusDetail { get; set; } = "";

        [JsonPropertyName("external_reference")]
        public string? ExternalReference { get; set; }

        [JsonPropertyName("transaction_amount")]
        public decimal TransactionAmount { get; set; }
    }
}
