using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/pedido/{accessToken}")]
public class ClientViewController : ControllerBase
{
    private readonly AppDbContext _db;

    public ClientViewController(AppDbContext db)
    {
        _db = db;
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
        // Si la delivery está InTransit, el status del pedido es "InTransit" (diferente a InRoute)
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

        return Ok(new ClientOrderView(
            ClientName: order.Client.Name,
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
            ClientLatitude: order.Client.Latitude,
            ClientLongitude: order.Client.Longitude,
            CreatedAt: order.CreatedAt
        ));
    }

    private ObjectResult Gone(string message)
    {
        return StatusCode(410, new { message });
    }
}
