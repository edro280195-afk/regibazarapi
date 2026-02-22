using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Hubs;
using EntregasApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/pedido/{accessToken}")]
public class ClientViewController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IHubContext<TrackingHub> _hub;

    public ClientViewController(AppDbContext db, IHubContext<TrackingHub> hub)
    {
        _db = db;
        _hub = hub;
    }

    /// <summary>GET /api/pedido/{token} - Vista pública del pedido</summary>
    [HttpGet]
    public async Task<ActionResult<ClientOrderView>> GetOrder(string accessToken)
    {
        var order = await _db.Orders
            .Include(o => o.Client)
            .Include(o => o.Items)
            .Include(o => o.DeliveryRoute)
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
            ClientType: finalType,                             // <--- 2. ¡Agregado!
            AdvancePayment: order.AdvancePayment
        ));
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
        if (order == null || order.DeliveryRouteId == null) return Ok(new List<object>());

        var delivery = await _db.Deliveries.FirstOrDefaultAsync(d => d.OrderId == order.Id);
        if (delivery == null) return Ok(new List<object>());

        if (order.Status == Models.OrderStatus.Delivered || order.Status == Models.OrderStatus.NotDelivered || order.Status == Models.OrderStatus.Canceled)
            return Ok(new List<object>());

        var msgs = await _db.ChatMessages
            .Where(m => m.DeliveryId == delivery.Id)
            .OrderBy(m => m.Timestamp)
            // 🚀 ANTÍDOTO: Creamos un objeto ligero sin relaciones para evitar el ciclo JSON
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
        if (order == null || order.DeliveryRouteId == null) return NotFound("Pedido no activo en ruta.");

        var delivery = await _db.Deliveries.FirstOrDefaultAsync(d => d.OrderId == order.Id);
        if (delivery == null) return NotFound();

        var msg = new ChatMessage
        {
            DeliveryRouteId = order.DeliveryRouteId.Value,
            DeliveryId = delivery.Id,
            Sender = "Client",
            Text = req.Text,
            Timestamp = DateTime.UtcNow
        };

        _db.ChatMessages.Add(msg);
        await _db.SaveChangesAsync();

        // 🚀 ANTÍDOTO: Empacamos solo los datos seguros
        var msgDto = new
        {
            id = msg.Id,
            deliveryRouteId = msg.DeliveryRouteId,
            deliveryId = msg.DeliveryId,
            sender = msg.Sender,
            text = msg.Text,
            timestamp = msg.Timestamp
        };

        var route = await _db.DeliveryRoutes.FindAsync(order.DeliveryRouteId);
        if (route != null)
        {
            await _hub.Clients.Group($"Route_{route.DriverToken}")
                .SendAsync("ReceiveClientChatMessage", msgDto); // Mandamos el ligero
        }

        return Ok(msgDto); // Regresamos el ligero
    }
    private ObjectResult Gone(string message)
    {
        return StatusCode(410, new { message });
    }
}