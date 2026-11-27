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

    public ClientViewController(AppDbContext db, IHubContext<DeliveryHub> hub, IPushNotificationService push)
    {
        _db = db;
        _hub = hub;
        _push = push;
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
            .Include(o => o.PaymentReports)
            .Include(o => o.Deliveries).ThenInclude(d => d.Evidences)
            .FirstOrDefaultAsync(o => o.AccessToken == accessToken);

        if (order == null)
            return NotFound("Pedido no encontrado.");

        var latestDelivery = order.DeliveryRouteId is int currentRouteId
            ? order.Deliveries.FirstOrDefault(d => d.DeliveryRouteId == currentRouteId)
            : order.Deliveries.OrderByDescending(d => d.Id).FirstOrDefault();

        var now = DateTime.UtcNow;
        if (!IsPublicLinkAvailable(order, now, latestDelivery?.Status == DeliveryStatus.NotDelivered))
            return Gone("Este enlace ha expirado.");

        // Ubicación del repartidor
        DriverLocationDto? driverLocation = null;
        if (order.DeliveryRoute?.Status == RouteStatus.Active &&
            order.DeliveryRoute.CurrentLatitude.HasValue &&
            order.DeliveryRoute.CurrentLongitude.HasValue)
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
        else if (latestDelivery?.Status == DeliveryStatus.NotDelivered)
        {
            // Los reintentos devuelven la orden a Pending, pero la clienta debe
            // seguir viendo el resultado del intento más reciente.
            clientStatus = "NotDelivered";
        }

        // --- LIMPIEZA DE TIPO DE CLIENTA ---
        string finalType = "Nueva";
        if (order.Client != null && !string.IsNullOrEmpty(order.Client.Type) && order.Client.Type != "None")
        {
            finalType = order.Client.Type;
        }

        var paymentVisible = IsPublicPaymentAvailable(order);
        var publicViewMode = GetPublicViewMode(order, paymentVisible, clientStatus);
        DateTime? estimatedArrival = publicViewMode == "Tracking"
            ? CalculateEstimatedArrival(driverLocation, order.Client?.Latitude, order.Client?.Longitude, deliveriesAhead)
            : (DateTime?)null;
        var publicAccessUntil = IsOpenPublicWorkflow(order) || latestDelivery?.Status == DeliveryStatus.NotDelivered
            ? (DateTime?)null
            : order.ExpiresAt;

        return Ok(new ClientOrderView(
            ClientId: order.ClientId,
            ClientName: order.Client?.Name ?? "Cliente",
            Items: order.Items.Select(i => new OrderItemDto(
                i.Id, i.ProductName, i.Quantity, i.UnitPrice, i.LineTotal
            )).ToList(),
            Subtotal: order.Subtotal,
            ShippingCost: order.ShippingCost,
            Total: order.Total,
            Status: clientStatus,
            EstimatedArrival: estimatedArrival,
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
            EvidenceUrls: latestDelivery?.Evidences
                .Where(e => e.Type == EvidenceType.DeliveryProof)
                .Select(e => e.ImagePath).ToList(),
            SignatureSvg: latestDelivery?.SignatureSvg,
            SignedByName: latestDelivery?.SignedByName,
            SignedAt: latestDelivery?.SignedAt,
            FailureReason: latestDelivery?.FailureReason,
            DeliveredAt: latestDelivery?.DeliveredAt,
            NonDeliveryEvidenceUrls: latestDelivery?.Evidences
                .Where(e => e.Type == EvidenceType.NonDeliveryProof)
                .Select(e => e.ImagePath).ToList(),
            PublicViewMode: publicViewMode,
            PublicAccessUntil: publicAccessUntil,
            PaymentVisible: paymentVisible,
            DeliveryTime: order.DeliveryTime,
            PaymentReports: (order.PaymentReports ?? new List<OrderPaymentReport>())
                .OrderByDescending(r => r.CreatedAt)
                .Select(r => new OrderPaymentReportDto(
                    r.Id, r.OrderId, r.Amount, r.Method, r.Reference, r.Notes,
                    r.ProofImageUrl, r.Status.ToString(), r.CreatedAt, r.ReviewedAt, r.RejectionReason))
                .ToList()
        ));
    }

    /// <summary>POST /api/pedido/{token}/confirm - La clienta confirma su pedido</summary>
    [HttpPost("confirm")]
    [AllowAnonymous] // Cualquier clienta con el link puede hacerlo
    public async Task<IActionResult> ConfirmOrder(string accessToken)
    {
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.AccessToken == accessToken);

        if (order == null) return NotFound(new { message = "Pedido no encontrado." });
        if (!IsPublicLinkAvailable(order, DateTime.UtcNow)) return StatusCode(410, new { message = "Este enlace ha expirado." });

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
        if (!IsPublicLinkAvailable(order, DateTime.UtcNow))
            return Gone("Este enlace ha expirado.");

        // Si el pedido está en ruta usamos ese intento; si espera reintento, mostramos
        // el último intento para conservar la conversación asociada.
        var delivery = order.DeliveryRouteId.HasValue
            ? await _db.Deliveries.FirstOrDefaultAsync(d =>
                d.OrderId == order.Id && d.DeliveryRouteId == order.DeliveryRouteId.Value)
            : await _db.Deliveries
                .Where(d => d.OrderId == order.Id)
                .OrderByDescending(d => d.Id)
                .FirstOrDefaultAsync();
        
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
        if (!IsPublicLinkAvailable(order, DateTime.UtcNow))
            return Gone("Este enlace ha expirado.");

        var delivery = order.DeliveryRouteId.HasValue
            ? await _db.Deliveries.FirstOrDefaultAsync(d =>
                d.OrderId == order.Id && d.DeliveryRouteId == order.DeliveryRouteId.Value)
            : await _db.Deliveries
                .Where(d => d.OrderId == order.Id)
                .OrderByDescending(d => d.Id)
                .FirstOrDefaultAsync();

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
    /// <summary>PATCH /api/pedido/{token}/instructions - Actualiza las instrucciones de entrega</summary>
    [HttpPatch("instructions")]
    [AllowAnonymous]
    public async Task<IActionResult> UpdateInstructions(string accessToken, [FromBody] UpdateInstructionsRequest req)
    {
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.AccessToken == accessToken);
        if (order == null) return NotFound(new { message = "Pedido no encontrado." });
        if (!IsPublicLinkAvailable(order, DateTime.UtcNow)) return StatusCode(410, new { message = "Este enlace ha expirado." });

        order.DeliveryInstructions = req.Instructions;
        await _db.SaveChangesAsync();

        return Ok(new { message = "Instrucciones actualizadas." });
    }

    /// <summary>POST /api/pedido/{token}/payment/card - La clienta paga con tarjeta (crédito o débito, física o digital) directo en la página</summary>
    [HttpPost("payment/card")]
    [AllowAnonymous]
    public async Task<ActionResult<CardPaymentResultDto>> PayWithCard(
        string accessToken,
        [FromBody] CardPaymentRequest req,
        [FromServices] IMercadoPagoOrderService mpOrderService)
    {
        if (string.IsNullOrWhiteSpace(req.CardToken) ||
            string.IsNullOrWhiteSpace(req.PaymentMethodId) ||
            req.Installments < 1)
            return BadRequest(new { message = "Datos de pago incompletos." });

        var order = await _db.Orders
            .Include(o => o.Payments)
            .Include(o => o.Client)
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.AccessToken == accessToken);

        if (order == null) return NotFound(new { message = "Pedido no encontrado." });
        if (!IsPublicLinkAvailable(order, DateTime.UtcNow)) return StatusCode(410, new { message = "Este enlace ha expirado." });
        if (!IsPublicPaymentAvailable(order)) return BadRequest(new { message = "Este pedido ya no acepta pagos desde este enlace." });

        if (order.BalanceDue <= 0m)
            return BadRequest(new { message = "Este pedido ya está liquidado." });

        try
        {
            var webhookBaseUrl = $"{Request.Scheme}://{Request.Host}";
            var result = await mpOrderService.ProcessOrderCardPaymentAsync(order, req, webhookBaseUrl);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(502, new { message = ex.Message });
        }
    }

    /// <summary>POST /api/pedido/{token}/payment/report - La clienta avisa que pagó (Transferencia u OXXO) con comprobante opcional</summary>
    [HttpPost("payment/report")]
    [AllowAnonymous]
    [RequestSizeLimit(8_000_000)]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<OrderPaymentReportDto>> ReportPayment(
        string accessToken,
        [FromForm] string method,
        [FromForm] decimal? amount,
        [FromForm] string? reference,
        [FromForm] string? notes,
        [FromForm] IFormFile? file,
        [FromServices] ICloudinaryService cloudinary)
    {
        var order = await _db.Orders
            .Include(o => o.Payments)
            .Include(o => o.Client)
            .FirstOrDefaultAsync(o => o.AccessToken == accessToken);

        if (order == null) return NotFound(new { message = "Pedido no encontrado." });
        if (!IsPublicLinkAvailable(order, DateTime.UtcNow)) return StatusCode(410, new { message = "Este enlace ha expirado." });
        if (!IsPublicPaymentAvailable(order)) return BadRequest(new { message = "Este pedido ya no acepta avisos de pago." });

        var reportedAmount = amount is > 0m ? Math.Round(amount.Value, 2, MidpointRounding.AwayFromZero) : Math.Round(order.BalanceDue, 2, MidpointRounding.AwayFromZero);
        if (reportedAmount <= 0m)
            return BadRequest(new { message = "Indica un monto válido." });

        var cleanMethod = string.IsNullOrWhiteSpace(method) ? "Transferencia" : method.Trim();

        string? proofUrl = null;
        if (file is { Length: > 0 })
        {
            if (file.Length > 8_000_000)
                return BadRequest(new { message = "La imagen del comprobante no puede superar 8 MB." });

            try
            {
                await using var stream = file.OpenReadStream();
                proofUrl = await cloudinary.UploadAsync(stream, file.FileName, "order-payment-proofs");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PaymentReport] Error subiendo comprobante: {ex.Message}");
                return StatusCode(500, new { message = "No se pudo guardar la foto del comprobante. Intenta de nuevo." });
            }
        }

        var report = new OrderPaymentReport
        {
            OrderId = order.Id,
            Amount = reportedAmount,
            Method = cleanMethod,
            Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(),
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            ProofImageUrl = proofUrl,
            Status = PaymentReportStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        _db.OrderPaymentReports.Add(report);
        await _db.SaveChangesAsync();

        var clientName = order.Client?.Name ?? "Clienta";
        await _hub.Clients.Group("Admins").SendAsync("DeliveryUpdate", new
        {
            OrderId = order.Id,
            Status = order.Status.ToString(),
            ChangeType = "payment_report",
            ClientName = clientName,
            AmountPaid = reportedAmount
        });

        await _push.SendNotificationToAdminsAsync(
            $"💸 Pago reportado: ${reportedAmount:F2} ({clientName})",
            $"Pedido #{order.Id} ({cleanMethod}). Revisa y confirma de recibido.",
            url: "/admin/orders",
            tag: "payment-report");

        return Ok(new OrderPaymentReportDto(
            report.Id, report.OrderId, report.Amount, report.Method, report.Reference,
            report.Notes, report.ProofImageUrl, report.Status.ToString(),
            report.CreatedAt, report.ReviewedAt, report.RejectionReason));
    }

    private static bool IsPublicPaymentAvailable(Order order)
        => order.Status != OrderStatus.Canceled && order.BalanceDue > 0m;

    private static bool IsOpenPublicWorkflow(Order order)
        => IsPublicPaymentAvailable(order)
           || order.DeliveryRoute?.Status == RouteStatus.Active
           || order.Status is OrderStatus.NotDelivered or OrderStatus.Postponed;

    private static bool IsPublicLinkAvailable(Order order, DateTime now, bool hasOpenDeliveryIssue = false)
        => hasOpenDeliveryIssue || IsOpenPublicWorkflow(order) || order.ExpiresAt >= now;

    private static string GetPublicViewMode(Order order, bool paymentVisible, string? clientStatus = null)
    {
        if (clientStatus == "NotDelivered")
            return "NotDelivered";

        if (order.Status == OrderStatus.Delivered)
            return paymentVisible ? "DeliveredWithBalance" : "Delivered";

        if (order.Status == OrderStatus.NotDelivered)
            return "NotDelivered";

        if (order.Status == OrderStatus.Postponed)
            return "Postponed";

        if (order.Status == OrderStatus.Canceled)
            return "Canceled";

        if (order.DeliveryRoute?.Status == RouteStatus.Active || order.Status == OrderStatus.InRoute)
            return "Tracking";

        return "Payment";
    }

    private static DateTime? CalculateEstimatedArrival(
        DriverLocationDto? driverLocation,
        double? clientLatitude,
        double? clientLongitude,
        int? deliveriesAhead)
    {
        if (driverLocation == null || !clientLatitude.HasValue || !clientLongitude.HasValue)
            return null;

        var distanceMeters = HaversineDistance(
            driverLocation.Latitude,
            driverLocation.Longitude,
            clientLatitude.Value,
            clientLongitude.Value);
        var drivingMinutes = Math.Max(5d, distanceMeters / 1000d / 25d * 60d);
        var stopMinutes = Math.Max(0, deliveriesAhead ?? 0) * 10d;

        return DateTime.UtcNow.AddMinutes(Math.Ceiling(drivingMinutes + stopMinutes));
    }

    private static double HaversineDistance(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        const double earthRadiusMeters = 6371000d;
        var latitudeDelta = (latitude2 - latitude1) * Math.PI / 180d;
        var longitudeDelta = (longitude2 - longitude1) * Math.PI / 180d;
        var a = Math.Sin(latitudeDelta / 2d) * Math.Sin(latitudeDelta / 2d)
                + Math.Cos(latitude1 * Math.PI / 180d)
                * Math.Cos(latitude2 * Math.PI / 180d)
                * Math.Sin(longitudeDelta / 2d)
                * Math.Sin(longitudeDelta / 2d);
        return earthRadiusMeters * 2d * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1d - a));
    }

    private ObjectResult Gone(string message)
    {
        return StatusCode(410, new { message });
    }
}
