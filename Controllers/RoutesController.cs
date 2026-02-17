using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class RoutesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITokenService _tokenService;
    private readonly IConfiguration _config;

    public RoutesController(AppDbContext db, ITokenService tokenService, IConfiguration config)
    {
        _db = db;
        _tokenService = tokenService;
        _config = config;
    }

    private string FrontendUrl => _config["App:FrontendUrl"] ?? "http://localhost:4200";

    /// <summary>POST /api/routes - Crear ruta con órdenes seleccionadas</summary>
    [HttpPost]
    public async Task<ActionResult<RouteDto>> Create(CreateRouteRequest req)
    {
        if (req.OrderIds == null || req.OrderIds.Count == 0)
            return BadRequest("Selecciona al menos una orden.");

        var orders = await _db.Orders
            .Include(o => o.Client)
            .Where(o => req.OrderIds.Contains(o.Id)
                        && o.Status == Models.OrderStatus.Pending
                        && o.OrderType == OrderType.Delivery) // <--- ¡AQUÍ ESTÁ EL FILTRO MÁGICO! 🚫 PickUp
            .ToListAsync();

        if (!orders.Any())
            return BadRequest("No se encontraron pedidos válidos para ruta (recuerda que los PickUp no se envían).");

        var route = new DeliveryRoute
        {
            DriverToken = _tokenService.GenerateAccessToken(),
            Status = RouteStatus.Pending
        };

        _db.DeliveryRoutes.Add(route);
        await _db.SaveChangesAsync();

        int sortOrder = 1;
        foreach (var order in orders)
        {
            order.DeliveryRouteId = route.Id;
            order.Status = Models.OrderStatus.InRoute;

            var delivery = new Delivery
            {
                OrderId = order.Id,
                DeliveryRouteId = route.Id,
                SortOrder = sortOrder++,
                Status = DeliveryStatus.Pending
            };
            _db.Deliveries.Add(delivery);
        }

        await _db.SaveChangesAsync();
        return Ok(await MapRouteDto(route.Id));
    }

    /// <summary>GET /api/routes - Listar rutas</summary>
    [HttpGet]
    public async Task<ActionResult<List<RouteDto>>> GetAll()
    {
        var routeIds = await _db.DeliveryRoutes
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => r.Id)
            .ToListAsync();

        var result = new List<RouteDto>();
        foreach (var id in routeIds)
            result.Add(await MapRouteDto(id));

        return Ok(result);
    }

    /// <summary>GET /api/routes/{id}</summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<RouteDto>> Get(int id)
    {
        var route = await _db.DeliveryRoutes.FindAsync(id);
        if (route == null) return NotFound();
        return Ok(await MapRouteDto(id));
    }

    private async Task<RouteDto> MapRouteDto(int routeId)
    {
        var route = await _db.DeliveryRoutes
            .FirstAsync(r => r.Id == routeId);

        var deliveries = await _db.Deliveries
            .Include(d => d.Order).ThenInclude(o => o.Client)
            .Include(d => d.Evidences)
            .Where(d => d.DeliveryRouteId == routeId)
            .OrderBy(d => d.SortOrder)
            .ToListAsync();

        return new RouteDto(
            Id: route.Id,
            DriverToken: route.DriverToken,
            DriverLink: $"{FrontendUrl}/repartidor/{route.DriverToken}",
            Status: route.Status.ToString(),
            CreatedAt: route.CreatedAt,
            StartedAt: route.StartedAt,
            Deliveries: deliveries.Select(d => new RouteDeliveryDto(
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
                EvidenceUrls: d.Evidences.Select(e => e.ImagePath).ToList()
            )).ToList()
        );
    }

    /// <summary>DELETE /api/routes/{id} - Cancelar ruta y liberar pedidos</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var route = await _db.DeliveryRoutes
            .Include(r => r.Deliveries)
            .ThenInclude(d => d.Order)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (route == null) return NotFound();

        foreach (var delivery in route.Deliveries)
        {
            if (delivery.Order.Status != Models.OrderStatus.Delivered)
            {
                delivery.Order.Status = Models.OrderStatus.Pending;
            }
        }

        // 3. Borrar la ruta
        _db.DeliveryRoutes.Remove(route);

        await _db.SaveChangesAsync();

        return NoContent();
    }
}
