using EntregasApi.Data;
using EntregasApi.DTOs;
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
[Route("api/pedido/{accessToken}")]
public class ClientViewController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IHubContext<DeliveryHub> _hub;
    private readonly IPushNotificationService _push;
    private readonly ICamiService _cami;

    public ClientViewController(AppDbContext db, IHubContext<DeliveryHub> hub, IPushNotificationService push, ICamiService cami)
    {
        _db = db;
        _hub = hub;
        _push = push;
        _cami = cami;
    }

    /// <summary>GET /api/pedido/{token} - Vista pública del pedido</summary>
    [HttpGet]
    public async Task<ActionResult<ClientOrderView>> GetOrder(string accessToken)
    {
        var order = await _db.Orders
            .Include(o => o.Client)
            .Include(o => o.Items)
            .Include(o => o.DeliveryRoute)
            .Include(o => o.Payments)
            .Include(o => o.Delivery).ThenInclude(d => d!.Evidences)
            .FirstOrDefaultAsync(o => o.AccessToken == accessToken);

        if (order == null)
            return NotFound("Pedido no encontrado.");

        if (order.ExpiresAt < DateTime.UtcNow)
            return Gone("Este enlace ha expirado.");

        // Ubicación del repartidor
        DriverLocationDto? driverLocation = null;
        if (order.DeliveryRoute?.Status == RouteStatus.Active &&
            order.DeliveryRoute.CurrentLatitude.HasValue)
        {
            driverLocation = new DriverLocationDto(
                order.DeliveryRoute.CurrentLatitude.Value,
                order.DeliveryRoute.CurrentLongitude!.Value,
                order.DeliveryRoute.LastLocationUpdate ?? DateTime.UtcNow
            );
        }

        // Info de posición en la ruta
        int? queuePosition = null;
        int? totalDeliveries = null;
        bool isCurrentDelivery = false;
        int? deliveriesAhead = null;

        if (order.DeliveryRouteId.HasValue)
        {
            var delivery = await _db.Deliveries
                .FirstOrDefaultAsync(d => d.OrderId == order.Id && d.DeliveryRouteId == order.DeliveryRouteId);

            if (delivery != null)
            {
                queuePosition = delivery.SortOrder;
                totalDeliveries = await _db.Deliveries
                    .CountAsync(d => d.DeliveryRouteId == order.DeliveryRouteId);

                isCurrentDelivery = delivery.Status == DeliveryStatus.InTransit;

                // Cuántas entregas hay antes de la mía que no se han completado
                deliveriesAhead = await _db.Deliveries
                    .CountAsync(d => d.DeliveryRouteId == order.DeliveryRouteId
                                     && d.SortOrder < delivery.SortOrder
                                     && (d.Status == DeliveryStatus.Pending || d.Status == DeliveryStatus.InTransit));
            }
        }

        // Determinar el status real para la clienta
        var clientStatus = order.Status.ToString();
        if (order.DeliveryRouteId.HasValue)
        {
            var myDelivery = await _db.Deliveries
                .FirstOrDefaultAsync(d => d.OrderId == order.Id && d.DeliveryRouteId == order.DeliveryRouteId);
            if (myDelivery?.Status == DeliveryStatus.InTransit)
            {
                clientStatus = "InTransit"; // Repartidor viene hacia esta clienta
            }
        }

        // --- LIMPIEZA DE TIPO DE CLIENTA ---
        string finalType = "Nueva";
        if (order.Client != null && !string.IsNullOrEmpty(order.Client.Type) && order.Client.Type != "None")
        {
            finalType = order.Client.Type;
        }

        return Ok(new ClientOrderView(
            ClientId: order.ClientId,                          // <--- 1. ¡Agregado!
            ClientName: order.Client?.Name ?? "Cliente",
            Items: order.Items.Select(i => new OrderItemDto(
                i.Id, i.ProductName, i.Quantity, i.UnitPrice, i.LineTotal
            )).ToList(),
            Subtotal: order.Subtotal,
            ShippingCost: order.ShippingCost,
            Total: order.Total,
            Status: clientStatus,
            EstimatedArrival: null,
            DriverLocation: driverLocation,
            QueuePosition: queuePosition,
            TotalDeliveries: totalDeliveries,
            IsCurrentDelivery: isCurrentDelivery,
            DeliveriesAhead: deliveriesAhead,

            ClientLatitude: order.Client?.Latitude,
            ClientLongitude: order.Client?.Longitude,
            ClientAddress: order.Client?.Address,
            CreatedAt: order.CreatedAt,
            Type: finalType,
            AdvancePayment: order.AdvancePayment,
            Payments: (order.Payments ?? new List<OrderPayment>())
                .Select(p => new OrderPaymentDto(p.Id, p.OrderId, p.Amount, p.Method, p.Date, p.RegisteredBy, p.Notes)).ToList(),
            AmountPaid: order.AmountPaid,
            BalanceDue: order.BalanceDue,
            ClientPoints: order.Client?.CurrentPoints ?? 0,
            DeliveryInstructions: order.DeliveryInstructions,
            ExpiresAt: order.ExpiresAt,
            ScheduledDeliveryDate: order.ScheduledDeliveryDate,
            EvidenceUrls: order.Delivery?.Evidences?.Select(e => e.ImagePath).ToList()
        ));
    }

    [HttpGet("cami-greeting")]
    public async Task<ActionResult<CamiGreetingResponse>> GetCamiGreeting(string accessToken)
    {
        var order = await _db.Orders
            .Include(o => o.Client)
            .Include(o => o.Items)
            .Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.AccessToken == accessToken);

        if (order == null) return NotFound("Pedido no encontrado.");

        var response = await _cami.GetProactiveGreetingAsync(order);
        return Ok(response);
    }

    /// <summary>POST /api/pedido/{token}/confirm - La clienta confirma su pedido</summary>
    [HttpPost("confirm")]
    [AllowAnonymous] // Cualquier clienta con el link puede hacerlo
    public async Task<IActionResult> ConfirmOrder(string accessToken)
    {
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.AccessToken == accessToken);

        if (order == null) return NotFound(new { message = "Pedido no encontrado." });
        if (order.ExpiresAt < DateTime.UtcNow) return StatusCode(410, new { message = "Este enlace ha expirado." });

        // Solo se puede confirmar si estaba Pendiente o Pospuesto
        if (order.Status == Models.OrderStatus.Pending || order.Status == Models.OrderStatus.Postponed)
        {
            order.Status = Models.OrderStatus.Confirmed;
            await _db.SaveChangesAsync();

            await _hub.Clients.Group("Admins").SendAsync("OrderConfirmed", new
            {
                OrderId = order.Id,
                ClientName = order.Client?.Name ?? "Clienta",
                NewStatus = "Confirmed"
            });

            // 🔔 Notificación Push a los Admins!
            await _push.SendNotificationToAdminsAsync(
                "💖 ¡Pedido Confirmado!",
                $"{order.Client?.Name} ha confirmado su pedido #{order.Id}. ¡A darle!",
                tag: "order-confirmed"
            );

            return Ok(new { message = "¡Pedido confirmado exitosamente! 💖" });
        }

        return BadRequest(new { message = $"El pedido ya se encuentra en estado: {order.Status}" });
    }

    // ═══════════════════════════════════════════
    //  CHAT CLIENTA ↔️ CHOFER
    // ═══════════════════════════════════════════

    [HttpGet("chat")]
    [AllowAnonymous]
    public async Task<IActionResult> GetChat(string accessToken)
    {
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.AccessToken == accessToken);
        if (order == null) return NotFound("Pedido no encontrado.");

        // Intentamos obtener el delivery asociado para filtrar los mensajes
        var delivery = await _db.Deliveries.FirstOrDefaultAsync(d => d.OrderId == order.Id);
        
        // Obtenemos los mensajes asociados a la entrega o a la ruta (si existe)
        var msgs = await _db.ChatMessages
            .Where(m => (m.DeliveryRouteId != null && m.DeliveryRouteId == order.DeliveryRouteId) || 
                        (delivery != null && m.DeliveryId == delivery.Id))
            .OrderBy(m => m.Timestamp)
            .Select(m => new {
                id = m.Id,
                deliveryRouteId = m.DeliveryRouteId,
                deliveryId = m.DeliveryId,
                sender = m.Sender,
                text = m.Text,
                timestamp = m.Timestamp
            })
            .ToListAsync();

        return Ok(msgs);
    }

    [HttpPost("chat")]
    [AllowAnonymous]
    public async Task<IActionResult> SendMessage(string accessToken, [FromBody] SendMessageRequest req)
    {
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.AccessToken == accessToken);
        if (order == null) return NotFound("Pedido no encontrado.");

        var delivery = await _db.Deliveries.FirstOrDefaultAsync(d => d.OrderId == order.Id);

        var msg = new ChatMessage
        {
            DeliveryRouteId = order.DeliveryRouteId,
            DeliveryId = delivery?.Id,
            Sender = "Client",
            Text = req.Text,
            Timestamp = DateTime.UtcNow
        };

        _db.ChatMessages.Add(msg);
        await _db.SaveChangesAsync();

        var msgDto = new
        {
            id = msg.Id,
            deliveryRouteId = msg.DeliveryRouteId,
            deliveryId = msg.DeliveryId,
            sender = msg.Sender,
            text = msg.Text,
            timestamp = msg.Timestamp
        };

        // Notificamos a los Administradores (Siempre)
        await _hub.Clients.Group("Admins")
            .SendAsync("ReceiveClientChatMessage", msgDto);

        // Si hay una ruta activa, notificamos al repartidor
        if (order.DeliveryRouteId.HasValue)
        {
            var route = await _db.DeliveryRoutes.FindAsync(order.DeliveryRouteId.Value);
            if (route != null)
            {
                await _hub.Clients.Group($"Route_{route.DriverToken}")
                    .SendAsync("ReceiveClientChatMessage", msgDto);
            }
        }

        return Ok(msgDto);
    }
    /// <summary>POST /api/pedido/{token}/payment/card - La clienta paga con tarjeta via Mercado Pago</summary>
    [HttpPost("payment/card")]
    [AllowAnonymous]
    public async Task<ActionResult<CardPaymentResultDto>> PayWithCard(
        string accessToken,
        [FromBody] CardPaymentRequest req,
        [FromServices] IHttpClientFactory httpClientFactory,
        [FromServices] IConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(req.CardToken) ||
            string.IsNullOrWhiteSpace(req.PaymentMethodId) ||
            req.Installments < 1)
            return BadRequest(new { message = "Datos de pago incompletos." });

        var order = await _db.Orders
            .Include(o => o.Payments)
            .Include(o => o.Client)
            .FirstOrDefaultAsync(o => o.AccessToken == accessToken);

        if (order == null) return NotFound(new { message = "Pedido no encontrado." });
        if (order.ExpiresAt < DateTime.UtcNow) return StatusCode(410, new { message = "Este enlace ha expirado." });

        // El monto viene SIEMPRE del servidor, nunca del cliente
        var balanceDue = order.BalanceDue;
        if (balanceDue <= 0)
            return BadRequest(new { message = "Este pedido ya está liquidado." });

        var mpAccessToken = config["MercadoPago:AccessToken"]
            ?? throw new InvalidOperationException("MercadoPago:AccessToken no configurado.");
        var idempotencyKey = Guid.NewGuid().ToString();

        using var httpClient = httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {mpAccessToken}");
        httpClient.DefaultRequestHeaders.Add("X-Idempotency-Key", idempotencyKey);

        // Armar issuer_id solo si viene y es numérico
        long? issuerId = long.TryParse(req.IssuerId, out var parsed) ? parsed : null;

        var paymentBody = new
        {
            transaction_amount = balanceDue,
            token = req.CardToken,
            description = $"Pedido #{order.Id} - Regi Bazar",
            installments = req.Installments,
            payment_method_id = req.PaymentMethodId,
            issuer_id = issuerId,
            payer = new { email = "pagos@regibazar.com" }
        };

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await httpClient.PostAsJsonAsync("https://api.mercadopago.com/v1/payments", paymentBody);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MercadoPago] Error de red: {ex.Message}");
            return StatusCode(502, new { message = "No se pudo conectar con Mercado Pago. Intenta de nuevo." });
        }

        MpPaymentApiResponse? mpResult;
        try
        {
            mpResult = await httpResponse.Content.ReadFromJsonAsync<MpPaymentApiResponse>(
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return StatusCode(502, new { message = "Respuesta inesperada de Mercado Pago." });
        }

        if (mpResult == null)
            return StatusCode(502, new { message = "Error al procesar la respuesta de Mercado Pago." });

        // Registrar pago solo si fue aprobado o en revisión
        if (mpResult.Status is "approved" or "in_process")
        {
            var payment = new OrderPayment
            {
                OrderId = order.Id,
                Amount = balanceDue,
                Method = "Tarjeta",
                RegisteredBy = "Cliente",
                Notes = $"MP#{mpResult.Id} | {mpResult.StatusDetail}"
            };
            _db.OrderPayments.Add(payment);

            // Si el pago fue completamente aprobado y el pedido aún no está en camino
            // ni entregado, lo confirmamos para que quede listo para entrega
            if (mpResult.Status == "approved" &&
                order.Status is Models.OrderStatus.Pending or
                               Models.OrderStatus.Postponed or
                               Models.OrderStatus.Confirmed)
            {
                order.Status = Models.OrderStatus.Confirmed;
            }

            await _db.SaveChangesAsync();

            await _hub.Clients.Group("Admins").SendAsync("DeliveryUpdate", new
            {
                OrderId = order.Id,
                Status = order.Status.ToString(),
                ChangeType = "card_payment",
                ClientName = order.Client?.Name,
                AmountPaid = balanceDue
            });

            var clientName = order.Client?.Name ?? "Clienta";
            await _push.SendNotificationToAdminsAsync(
                $"💳 {clientName} pagó ${balanceDue:F2} con tarjeta",
                $"Pedido #{order.Id} liquidado y listo para entrega 🛵",
                tag: "card-payment");
        }

        var message = mpResult.Status switch
        {
            "approved"   => "¡Pago aprobado con éxito! 🎉",
            "in_process" => "Tu pago está en revisión. Recibirás confirmación pronto.",
            "pending"    => "Pago pendiente de confirmación.",
            "rejected"   => GetRejectionMessage(mpResult.StatusDetail),
            _            => "No se pudo procesar el pago. Intenta con otra tarjeta."
        };

        return Ok(new CardPaymentResultDto(mpResult.Status, mpResult.StatusDetail, balanceDue, message, mpResult.Id));
    }

    private static string GetRejectionMessage(string statusDetail) => statusDetail switch
    {
        "cc_rejected_insufficient_amount"    => "Fondos insuficientes. Verifica tu saldo.",
        "cc_rejected_bad_filled_card_number" => "Número de tarjeta incorrecto.",
        "cc_rejected_bad_filled_date"        => "Fecha de vencimiento incorrecta.",
        "cc_rejected_bad_filled_security_code" => "Código de seguridad incorrecto.",
        "cc_rejected_blacklist"              => "Tarjeta no autorizada. Intenta con otra.",
        "cc_rejected_call_for_authorize"     => "Tu banco requiere autorización. Llama a tu banco.",
        "cc_rejected_card_disabled"          => "Tarjeta deshabilitada. Comunícate con tu banco.",
        "cc_rejected_duplicated_payment"     => "Pago duplicado detectado.",
        _                                    => "Pago rechazado. Intenta con otra tarjeta o método de pago."
    };

    private ObjectResult Gone(string message)
    {
        return StatusCode(410, new { message });
    }

    private sealed class MpPaymentApiResponse
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("status_detail")]
        public string StatusDetail { get; set; } = "";
    }
}