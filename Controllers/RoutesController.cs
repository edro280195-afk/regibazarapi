using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Hubs; // <--- Importante
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
    private readonly IHubContext<TrackingHub> _hub;

    public RoutesController(AppDbContext db, ITokenService tokenService, IConfiguration config, IHubContext<TrackingHub> hub)
    {
        _db = db;
        _tokenService = tokenService;
        _config = config;
        _hub = hub;
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
                        && (o.Status == Models.OrderStatus.Pending || o.Status == Models.OrderStatus.Confirmed || o.Status == Models.OrderStatus.Shipped)
                        && o.OrderType == OrderType.Delivery)
            .ToListAsync();

        if (!orders.Any())
            return BadRequest("No se encontraron pedidos válidos para ruta (recuerda que los PickUp no se envían).");

        var route = new DeliveryRoute
        {
            DriverToken = _tokenService.GenerateAccessToken(),
            Status = RouteStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            // Asignamos un nombre default si no viene uno (puedes mejorarlo recibiéndolo del req)
            Name = $"Ruta {DateTime.Now:dd/MM HH:mm}",
            ScheduledDate = DateTime.SpecifyKind(DateTime.Today, DateTimeKind.Utc)
        };

        _db.DeliveryRoutes.Add(route);
        await _db.SaveChangesAsync();

        // Ordenamos la lista de la db para que coincida exactamente con el orden recibido del optimizador
        var sortedOrders = req.OrderIds
            .Select(id => orders.FirstOrDefault(o => o.Id == id))
            .Where(o => o != null)
            .ToList();

        int sortOrder = 1;
        foreach (var order in sortedOrders)
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
            .Take(50) // Limitamos a 50 para optimizar
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

    // Asegúrate de que el constructor use IHubContext<TrackingHub>
    // private readonly IHubContext<TrackingHub> _hub;

    [HttpGet("{id}/chat")]
    public async Task<IActionResult> GetRouteChat(int id)
    {
        var msgs = await _db.ChatMessages
            // 🚀 FILTRO CLAVE: Solo mensajes donde DeliveryId sea NULL (Admin <-> Chofer)
            .Where(m => m.DeliveryRouteId == id && m.DeliveryId == null)
            .OrderBy(m => m.Timestamp)
            .Select(m => new {
                id = m.Id,
                sender = m.Sender,
                text = m.Text,
                timestamp = m.Timestamp,
                deliveryRouteId = m.DeliveryRouteId
            })
            .ToListAsync();

        return Ok(msgs);
    }

    [HttpPost("{id}/chat")]
    public async Task<IActionResult> SendAdminMessage(int id, [FromBody] SendMessageRequest req)
    {
        var route = await _db.DeliveryRoutes.FindAsync(id);
        if (route == null) return NotFound("Ruta no encontrada");

        var msg = new ChatMessage
        {
            DeliveryRouteId = route.Id,
            Sender = "Admin", // El Admin siempre es el Admin
            Text = req.Text,
            Timestamp = DateTime.UtcNow,
            DeliveryId = null // 🚀 IMPORTANTE: Forzamos que sea nulo para que no se filtre a las clientas
        };

        _db.ChatMessages.Add(msg);
        await _db.SaveChangesAsync();

        // Creamos el objeto ligero (Antídoto para el Error 500)
        var msgDto = new
        {
            id = msg.Id,
            sender = msg.Sender,
            text = msg.Text,
            timestamp = msg.Timestamp,
            deliveryRouteId = msg.DeliveryRouteId
        };

        // 🔔 Avisamos al chofer (Usando el Hub unificado)
        await _hub.Clients.Group($"Route_{route.DriverToken}")
            .SendAsync("ReceiveChatMessage", msgDto);

        return Ok(msgDto);
    }

    // ═══════════════════════════════════════════
    //  HELPERS & DELETE
    // ═══════════════════════════════════════════

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

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        // 1. Buscamos la ruta y sus entregas
        var route = await _db.DeliveryRoutes
            .Include(r => r.Deliveries)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (route == null) return NotFound();

        // 2. 🚀 ATAQUE DIRECTO: Buscamos las órdenes en su propia tabla y las soltamos
        var linkedOrders = await _db.Orders.Where(o => o.DeliveryRouteId == id).ToListAsync();

        if (linkedOrders.Any())
        {
            foreach (var order in linkedOrders)
            {
                order.DeliveryRouteId = null; // Rompemos la cadena

                if (order.Status != Models.OrderStatus.Delivered)
                {
                    order.Status = Models.OrderStatus.Pending;
                }
            }

            // Obligamos a EF Core a guardar ESTO antes de siquiera pensar en borrar la ruta
            await _db.SaveChangesAsync();
        }

        // 3. Limpiar ChatMessages
        var chats = await _db.ChatMessages.Where(c => c.DeliveryRouteId == id).ToListAsync();
        if (chats.Any())
        {
            _db.ChatMessages.RemoveRange(chats);
        }

        // 4. Limpiar Gastos (Descomenta esto si tienes DriverExpenses en tu DbContext)
        /*
        var expenses = await _db.DriverExpenses.Where(e => e.DriverRouteId == id).ToListAsync();
        if (expenses.Any()) 
        {
            _db.DriverExpenses.RemoveRange(expenses);
        }
        */

        // 5. Borrar los registros intermedios de entregas
        if (route.Deliveries.Any())
        {
            _db.Deliveries.RemoveRange(route.Deliveries);
        }

        // 6. Ahora sí, la ruta está completamente huérfana. Le damos cuello.
        _db.DeliveryRoutes.Remove(route);

        await _db.SaveChangesAsync();

        return NoContent();
    }
}

// DTO auxiliar para el chat
