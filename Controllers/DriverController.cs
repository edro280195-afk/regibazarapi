using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Hubs;
using EntregasApi.Models;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/driver/{driverToken}")]
public class DriverController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IHubContext<TrackingHub> _hub;
    private readonly IWebHostEnvironment _env;

    public DriverController(AppDbContext db, IHubContext<TrackingHub> hub, IWebHostEnvironment env)
    {
        _db = db;
        _hub = hub;
        _env = env;
    }

    /// <summary>GET /api/driver/{token} - Obtener ruta del repartidor</summary>
    [HttpGet]
    public async Task<IActionResult> GetRoute(string driverToken)
    {
        var route = await _db.DeliveryRoutes
            .FirstOrDefaultAsync(r => r.DriverToken == driverToken);

        if (route == null) return NotFound("Ruta no encontrada.");

        var deliveries = await _db.Deliveries
            .Include(d => d.Order).ThenInclude(o => o.Client)
            .Include(d => d.Evidences)
            .Where(d => d.DeliveryRouteId == route.Id)
            .OrderBy(d => d.SortOrder)
            .ToListAsync();

        return Ok(new
        {
            route.Id,
            Status = route.Status.ToString(),
            route.StartedAt,
            Deliveries = deliveries.Select(d => new RouteDeliveryDto(
                DeliveryId: d.Id,
                OrderId: d.OrderId,
                SortOrder: d.SortOrder,
                ClientName: d.Order.Client.Name,
                ClientAddress: d.Order.Client.Address,
                Latitude: d.Order.Client.Latitude,
                Longitude: d.Order.Client.Longitude,
                Status: d.Status.ToString(),
                Total: d.Order.Total,
                DeliveredAt: d.DeliveredAt,
                Notes: d.Notes,
                FailureReason: d.FailureReason,
                EvidenceUrls: d.Evidences.Select(e => $"/uploads/{e.ImagePath}").ToList()
            )).ToList()
        });
    }

    /// <summary>POST /api/driver/{token}/start - Iniciar ruta</summary>
    [HttpPost("start")]
    public async Task<IActionResult> StartRoute(string driverToken)
    {
        var route = await _db.DeliveryRoutes
            .FirstOrDefaultAsync(r => r.DriverToken == driverToken);

        if (route == null) return NotFound();
        if (route.Status != RouteStatus.Pending)
            return BadRequest("La ruta ya fue iniciada o completada.");

        route.Status = RouteStatus.Active;
        route.StartedAt = DateTime.UtcNow;

        // Auto-marcar la primera entrega como InTransit
        var firstDelivery = await _db.Deliveries
            .Include(d => d.Order)
            .Where(d => d.DeliveryRouteId == route.Id)
            .OrderBy(d => d.SortOrder)
            .FirstOrDefaultAsync();

        if (firstDelivery != null)
        {
            firstDelivery.Status = DeliveryStatus.InTransit;

            // Notificar a la primera clienta que el repartidor va en camino
            await _hub.Clients.Group($"order_{firstDelivery.Order.AccessToken}")
                .SendAsync("DeliveryUpdate", new
                {
                    Status = "InTransit",
                    Message = "¡El repartidor va en camino hacia ti!"
                });
        }

        await _db.SaveChangesAsync();

        // Notificar al admin
        await _hub.Clients.Group("admin")
            .SendAsync("RouteStarted", new { RouteId = route.Id });

        return Ok(new { message = "Ruta iniciada.", firstDeliveryId = firstDelivery?.Id });
    }

    /// <summary>POST /api/driver/{token}/transit/{deliveryId} - Marcar entrega como "en camino"</summary>
    [HttpPost("transit/{deliveryId}")]
    public async Task<IActionResult> MarkInTransit(string driverToken, int deliveryId)
    {
        var route = await _db.DeliveryRoutes
            .FirstOrDefaultAsync(r => r.DriverToken == driverToken);
        if (route == null) return NotFound("Ruta no encontrada.");
        if (route.Status != RouteStatus.Active)
            return BadRequest("La ruta no está activa.");

        var delivery = await _db.Deliveries
            .Include(d => d.Order)
            .FirstOrDefaultAsync(d => d.Id == deliveryId && d.DeliveryRouteId == route.Id);
        if (delivery == null) return NotFound("Entrega no encontrada.");
        if (delivery.Status != DeliveryStatus.Pending)
            return BadRequest("Esta entrega ya fue procesada.");

        // Quitar InTransit de cualquier otra entrega en esta ruta (solo 1 activa a la vez)
        var previousInTransit = await _db.Deliveries
            .Where(d => d.DeliveryRouteId == route.Id && d.Status == DeliveryStatus.InTransit)
            .ToListAsync();

        foreach (var prev in previousInTransit)
        {
            prev.Status = DeliveryStatus.Pending;
        }

        delivery.Status = DeliveryStatus.InTransit;
        await _db.SaveChangesAsync();

        // Notificar a la clienta específica que el repartidor viene hacia ella
        await _hub.Clients.Group($"order_{delivery.Order.AccessToken}")
            .SendAsync("DeliveryUpdate", new
            {
                Status = "InTransit",
                Message = "¡El repartidor va en camino hacia ti!"
            });

        // Notificar a las clientas anteriores (InTransit → Pending) que ya no son la activa
        foreach (var prev in previousInTransit)
        {
            var prevOrder = await _db.Orders.FindAsync(prev.OrderId);
            if (prevOrder != null)
            {
                await _hub.Clients.Group($"order_{prevOrder.AccessToken}")
                    .SendAsync("DeliveryUpdate", new { Status = "InRoute" });
            }
        }

        // Notificar al admin
        await _hub.Clients.Group("admin")
            .SendAsync("DeliveryInTransit", new
            {
                delivery.Id,
                delivery.OrderId,
                RouteId = route.Id
            });

        return Ok(new { message = "Entrega marcada en tránsito." });
    }

    /// <summary>POST /api/driver/{token}/location - Actualizar ubicación GPS</summary>
    [HttpPost("location")]
    public async Task<IActionResult> UpdateLocation(string driverToken, UpdateLocationRequest req)
    {
        var route = await _db.DeliveryRoutes
            .Include(r => r.Orders)
            .FirstOrDefaultAsync(r => r.DriverToken == driverToken);

        if (route == null) return NotFound();

        route.CurrentLatitude = req.Latitude;
        route.CurrentLongitude = req.Longitude;
        route.LastLocationUpdate = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Enviar ubicación a todas las clientas de esta ruta via SignalR
        var orderTokens = await _db.Orders
            .Where(o => o.DeliveryRouteId == route.Id)
            .Select(o => o.AccessToken)
            .ToListAsync();

        foreach (var token in orderTokens)
        {
            await _hub.Clients.Group($"order_{token}").SendAsync("LocationUpdate", new
            {
                req.Latitude,
                req.Longitude,
                Timestamp = DateTime.UtcNow
            });
        }

        // También notificar al panel admin
        await _hub.Clients.Group("admin").SendAsync("DriverLocation", new
        {
            RouteId = route.Id,
            req.Latitude,
            req.Longitude,
            Timestamp = DateTime.UtcNow
        });

        return Ok();
    }

    /// <summary>POST /api/driver/{token}/deliver/{deliveryId} - Marcar como entregado</summary>
    [HttpPost("deliver/{deliveryId}")]
    public async Task<IActionResult> MarkDelivered(string driverToken, int deliveryId,
        [FromForm] CompleteDeliveryRequest req, [FromForm] List<IFormFile>? photos)
    {
        var route = await _db.DeliveryRoutes
            .FirstOrDefaultAsync(r => r.DriverToken == driverToken);
        if (route == null) return NotFound("Ruta no encontrada.");

        var delivery = await _db.Deliveries
            .Include(d => d.Order)
            .FirstOrDefaultAsync(d => d.Id == deliveryId && d.DeliveryRouteId == route.Id);
        if (delivery == null) return NotFound("Entrega no encontrada.");

        delivery.Status = DeliveryStatus.Delivered;
        delivery.DeliveredAt = DateTime.UtcNow;
        delivery.Notes = req.Notes;
        delivery.Order.Status = Models.OrderStatus.Delivered;

        // Guardar fotos
        if (photos != null)
            await SavePhotos(delivery, photos, EvidenceType.DeliveryProof);

        await _db.SaveChangesAsync();

        // Notificar a la clienta
        await _hub.Clients.Group($"order_{delivery.Order.AccessToken}")
            .SendAsync("DeliveryUpdate", new { Status = "Delivered", delivery.DeliveredAt });

        // Notificar al admin
        await _hub.Clients.Group("admin")
            .SendAsync("DeliveryCompleted", new
            {
                delivery.Id,
                delivery.OrderId,
                Status = "Delivered",
                delivery.DeliveredAt
            });

        // Auto-avanzar: marcar la siguiente entrega pendiente como InTransit
        var nextDeliveryId = await AutoAdvanceToNext(route.Id, delivery.SortOrder);

        // Verificar si la ruta está completa
        await CheckRouteCompletion(route.Id);

        return Ok(new { message = "Entrega registrada.", nextDeliveryId });
    }

    /// <summary>POST /api/driver/{token}/fail/{deliveryId} - Marcar como no entregado</summary>
    [HttpPost("fail/{deliveryId}")]
    public async Task<IActionResult> MarkFailed(string driverToken, int deliveryId,
        [FromForm] FailDeliveryRequest req, [FromForm] List<IFormFile>? photos)
    {
        var route = await _db.DeliveryRoutes
            .FirstOrDefaultAsync(r => r.DriverToken == driverToken);
        if (route == null) return NotFound("Ruta no encontrada.");

        var delivery = await _db.Deliveries
            .Include(d => d.Order)
            .FirstOrDefaultAsync(d => d.Id == deliveryId && d.DeliveryRouteId == route.Id);
        if (delivery == null) return NotFound("Entrega no encontrada.");

        delivery.Status = DeliveryStatus.NotDelivered;
        delivery.FailureReason = req.Reason;
        delivery.Notes = req.Notes;
        delivery.DeliveredAt = DateTime.UtcNow;
        delivery.Order.Status = Models.OrderStatus.NotDelivered;

        if (photos != null)
            await SavePhotos(delivery, photos, EvidenceType.NonDeliveryProof);

        await _db.SaveChangesAsync();

        await _hub.Clients.Group($"order_{delivery.Order.AccessToken}")
            .SendAsync("DeliveryUpdate", new { Status = "NotDelivered" });

        await _hub.Clients.Group("admin")
            .SendAsync("DeliveryFailed", new
            {
                delivery.Id,
                delivery.OrderId,
                Status = "NotDelivered",
                delivery.FailureReason
            });

        // Auto-avanzar a la siguiente
        var nextDeliveryId = await AutoAdvanceToNext(route.Id, delivery.SortOrder);

        await CheckRouteCompletion(route.Id);

        return Ok(new { message = "No-entrega registrada.", nextDeliveryId });
    }

    /// <summary>Auto-avanza la siguiente entrega pendiente a InTransit</summary>
    private async Task<int?> AutoAdvanceToNext(int routeId, int currentSortOrder)
    {
        var nextDelivery = await _db.Deliveries
            .Include(d => d.Order)
            .Where(d => d.DeliveryRouteId == routeId
                        && d.Status == DeliveryStatus.Pending
                        && d.SortOrder > currentSortOrder)
            .OrderBy(d => d.SortOrder)
            .FirstOrDefaultAsync();

        if (nextDelivery == null)
        {
            // Si no hay siguiente después, buscar cualquier pendiente (por si saltaron orden)
            nextDelivery = await _db.Deliveries
                .Include(d => d.Order)
                .Where(d => d.DeliveryRouteId == routeId && d.Status == DeliveryStatus.Pending)
                .OrderBy(d => d.SortOrder)
                .FirstOrDefaultAsync();
        }

        if (nextDelivery != null)
        {
            nextDelivery.Status = DeliveryStatus.InTransit;
            await _db.SaveChangesAsync();

            // Notificar a la siguiente clienta
            await _hub.Clients.Group($"order_{nextDelivery.Order.AccessToken}")
                .SendAsync("DeliveryUpdate", new
                {
                    Status = "InTransit",
                    Message = "¡El repartidor va en camino hacia ti!"
                });

            return nextDelivery.Id;
        }

        return null;
    }

    private async Task SavePhotos(Delivery delivery, List<IFormFile> photos, EvidenceType type)
    {
        var uploadDir = Path.Combine(_env.ContentRootPath, "uploads", "evidence");
        Directory.CreateDirectory(uploadDir);

        foreach (var photo in photos.Where(p => p.Length > 0))
        {
            var fileName = $"{delivery.Id}_{Guid.NewGuid():N}{Path.GetExtension(photo.FileName)}";
            var filePath = Path.Combine(uploadDir, fileName);

            using var stream = new FileStream(filePath, FileMode.Create);
            await photo.CopyToAsync(stream);

            _db.DeliveryEvidences.Add(new DeliveryEvidence
            {
                DeliveryId = delivery.Id,
                ImagePath = $"evidence/{fileName}",
                Type = type
            });
        }
    }

    private async Task CheckRouteCompletion(int routeId)
    {
        var allDone = !await _db.Deliveries
            .AnyAsync(d => d.DeliveryRouteId == routeId
                          && (d.Status == DeliveryStatus.Pending || d.Status == DeliveryStatus.InTransit));

        if (allDone)
        {
            var route = await _db.DeliveryRoutes.FindAsync(routeId);
            if (route != null)
            {
                route.Status = RouteStatus.Completed;
                route.CompletedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();

                await _hub.Clients.Group("admin")
                    .SendAsync("RouteCompleted", new { RouteId = routeId });
            }
        }
    }
}
