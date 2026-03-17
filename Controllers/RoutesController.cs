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
    private readonly IHubContext<DeliveryHub> _hub;
    private readonly IPushNotificationService _push;
    private readonly IGeminiService _geminiService;
    private readonly IGoogleTtsService _tts;

    public RoutesController(AppDbContext db, ITokenService tokenService, IConfiguration config, IHubContext<DeliveryHub> hub, IPushNotificationService push, IGeminiService geminiService, IGoogleTtsService tts)
    {
        _db = db;
        _tokenService = tokenService;
        _config = config;
        _hub = hub;
        _push = push;
        _geminiService = geminiService;
        _tts = tts;
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

            // 🔔 Notificar a la clienta que su pedido ya está en ruta
            await _push.NotifyClientDriverEnRouteAsync(order.ClientId);
        }

        await _db.SaveChangesAsync();
        return Ok(await MapRouteDto(route.Id));
    }

    /// <summary>POST /api/routes/ai-select - Gemini elige rutas por voz</summary>
    [HttpPost("ai-select")]
    public async Task<ActionResult<AiRouteSelectionResponse>> AiSelectRoute([FromBody] AiRouteSelectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.VoiceCommand) || request.AvailableOrders == null)
            return BadRequest("Faltan datos de voz o las órdenes disponibles.");

        try
        {
            var response = await _geminiService.SelectOrdersForRouteAsync(request);
            
            // Sintetizamos la confirmación por voz
            string? audioBase64 = null;
            try
            {
                audioBase64 = await _tts.SynthesizeAsync(response.AiConfirmationMessage);
            }
            catch (Exception ttsEx)
            {
                // No bloqueamos la respuesta si falla el audio
            }

            return Ok(response with { AudioBase64 = audioBase64 });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Error al procesar la instrucción de voz.", detail = ex.Message });
        }
    }

    /// <summary>GET /api/routes - Listar rutas optimizado</summary>
    [HttpGet]
    public async Task<ActionResult<List<RouteDto>>> GetAll()
    {
        // 1. Traer las últimas 50 rutas con sus relaciones principales en una sola consulta
        var routes = await _db.DeliveryRoutes
            .Include(r => r.Deliveries).ThenInclude(d => d.Order).ThenInclude(o => o.Client)
            .Include(r => r.Deliveries).ThenInclude(d => d.Order).ThenInclude(o => o.Payments)
            .Include(r => r.Deliveries).ThenInclude(d => d.Order).ThenInclude(o => o.Items)
            .Include(r => r.Deliveries).ThenInclude(d => d.Evidences)
            .OrderByDescending(r => r.CreatedAt)
            .Take(50)
            .ToListAsync();

        // 2. Traer todos los gastos de estas rutas de golpe
        var routeIds = routes.Select(r => r.Id).ToList();
        var allExpenses = await _db.DriverExpenses
            .Where(e => routeIds.Contains((int)e.DeliveryRouteId))
            .ToListAsync();

        // 3. Mapear a DTOs en memoria sin más llamadas a DB
        var result = routes.Select(route => new RouteDto(
            Id: route.Id,
            DriverToken: route.DriverToken,
            DriverLink: $"{FrontendUrl}/repartidor/{route.DriverToken}",
            Status: route.Status.ToString(),
            CreatedAt: route.CreatedAt,
            StartedAt: route.StartedAt,
            Deliveries: route.Deliveries.OrderBy(d => d.SortOrder).Select(d => new RouteDeliveryDto(
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
                EvidenceUrls: d.Evidences.Select(e => e.ImagePath).ToList(),
                ClientPhone: d.Order.Client.Phone,
                PaymentMethod: d.Order.PaymentMethod,
                Payments: (d.Order.Payments ?? new List<OrderPayment>())
                    .Select(p => new OrderPaymentDto(p.Id, p.OrderId, p.Amount, p.Method, p.Date, p.RegisteredBy, p.Notes)).ToList(),
                Items: (d.Order.Items ?? new List<OrderItem>())
                    .Select(i => new OrderItemDto(i.Id, i.ProductName, i.Quantity, i.UnitPrice, i.LineTotal)).ToList(),
                AmountPaid: d.Order.AmountPaid,
                BalanceDue: d.Order.BalanceDue,
                DeliveryInstructions: d.Order.Client.DeliveryInstructions
            )).ToList(),
            Expenses: allExpenses
                .Where(e => e.DeliveryRouteId == route.Id)
                .Select(e => new DriverExpenseDto(
                    e.Id,
                    e.DeliveryRouteId,
                    null,
                    e.Amount,
                    e.ExpenseType,
                    e.Date,
                    e.Notes,
                    e.EvidencePath,
                    e.CreatedAt
                )).ToList()
        )).ToList();

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
    //  REORDENAMIENTO MANUAL (DRAG & DROP)
    // ═══════════════════════════════════════════
    [HttpPut("{id}/reorder")]
    public async Task<IActionResult> ReorderDeliveries(int id, [FromBody] List<int> deliveryIdsInOrder)
    {
        var route = await _db.DeliveryRoutes
            .Include(r => r.Deliveries)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (route == null) return NotFound("Ruta no encontrada");

        // Actualizamos cada delivery al nuevo orden iterando la lista enviada
        int newOrder = 1;
        foreach (var deliveryId in deliveryIdsInOrder)
        {
            var delivery = route.Deliveries.FirstOrDefault(d => d.Id == deliveryId);
            if (delivery != null)
            {
                delivery.SortOrder = newOrder++;
            }
        }

        await _db.SaveChangesAsync();

        // 🔔 Avisar al Chofer que su lista cambió
        await _hub.Clients.Group($"Route_{route.DriverToken}")
            .SendAsync("RouteUpdated");

        return Ok(new { Message = "Orden actualizado correctamente" });
    }

    // ═══════════════════════════════════════════
    //  MUTACIÓN ATÓMICA DE RUTA 🛠️
    // ═══════════════════════════════════════════

    [HttpPost("{id}/add-order")]
    public async Task<IActionResult> AddOrderToRoute(int id, [FromBody] int orderId)
    {
        var route = await _db.DeliveryRoutes.Include(r => r.Deliveries).FirstOrDefaultAsync(r => r.Id == id);
        if (route == null) return NotFound("Ruta no encontrada");

        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
        if (order == null) return NotFound("Orden no encontrada");

        if (order.DeliveryRouteId != null) return BadRequest("La orden ya tiene una ruta asignada.");

        order.DeliveryRouteId = id;
        order.Status = Models.OrderStatus.InRoute;

        int nextSortOrder = route.Deliveries.Any() ? route.Deliveries.Max(d => d.SortOrder) + 1 : 1;

        var delivery = new Delivery
        {
            OrderId = orderId,
            DeliveryRouteId = id,
            SortOrder = nextSortOrder,
            Status = DeliveryStatus.Pending
        };
        _db.Deliveries.Add(delivery);
        await _db.SaveChangesAsync();

        // 🔔 Avisar al chofer
        await _hub.Clients.Group($"Route_{route.DriverToken}").SendAsync("RouteUpdated");

        // 🔔 Notificar a la clienta
        await _push.NotifyClientDriverEnRouteAsync(order.ClientId);

        return Ok(new { Message = "Orden agregada correctamente" });
    }

    [HttpDelete("{id}/remove-order/{orderId}")]
    public async Task<IActionResult> RemoveOrderFromRoute(int id, int orderId)
    {
        var route = await _db.DeliveryRoutes.Include(r => r.Deliveries).FirstOrDefaultAsync(r => r.Id == id);
        if (route == null) return NotFound("Ruta no encontrada");

        var delivery = await _db.Deliveries.FirstOrDefaultAsync(d => d.DeliveryRouteId == id && d.OrderId == orderId);
        if (delivery == null) return NotFound("Entrega no encontrada en esta ruta.");

        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
        if (order != null)
        {
            order.DeliveryRouteId = null;
            order.Status = Models.OrderStatus.Pending;
        }

        _db.Deliveries.Remove(delivery);
        await _db.SaveChangesAsync();

        // 🔔 Avisar al chofer
        await _hub.Clients.Group($"Route_{route.DriverToken}").SendAsync("RouteUpdated");

        return Ok(new { Message = "Orden eliminada de la ruta correctamente" });
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
            .Include(d => d.Order).ThenInclude(o => o.Payments)
            .Include(d => d.Order).ThenInclude(o => o.Items)
            .Include(d => d.Evidences)
            .Where(d => d.DeliveryRouteId == routeId)
            .OrderBy(d => d.SortOrder)
            .ToListAsync();

        var expenses = await _db.DriverExpenses
            .Where(e => e.DeliveryRouteId == routeId)
            .Select(e => new DriverExpenseDto(
                e.Id,
                e.DeliveryRouteId,
                null,
                e.Amount,
                e.ExpenseType,
                e.Date,
                e.Notes,
                e.EvidencePath,
                e.CreatedAt
            ))
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
                EvidenceUrls: d.Evidences.Select(e => e.ImagePath).ToList(),
                ClientPhone: d.Order.Client.Phone,
                PaymentMethod: d.Order.PaymentMethod,
                Payments: (d.Order.Payments ?? new List<OrderPayment>())
                    .Select(p => new OrderPaymentDto(p.Id, p.OrderId, p.Amount, p.Method, p.Date, p.RegisteredBy, p.Notes)).ToList(),
                Items: (d.Order.Items ?? new List<OrderItem>())
                    .Select(i => new OrderItemDto(i.Id, i.ProductName, i.Quantity, i.UnitPrice, i.LineTotal)).ToList(),
                AmountPaid: d.Order.AmountPaid,
                BalanceDue: d.Order.BalanceDue
            )).ToList(),
            Expenses: expenses
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

    // ═══════════════════════════════════════════
    //  LIQUIDACIÓN DE RUTA (CORTE DE CAJA)
    // ═══════════════════════════════════════════
    [HttpPost("{id}/liquidate")]
    public async Task<IActionResult> LiquidateRoute(int id)
    {
        var route = await _db.DeliveryRoutes.FindAsync(id);
        if (route == null) return NotFound("Ruta no encontrada");

        // Cambiamos el estado de la ruta a Completed
        route.Status = RouteStatus.Completed;
        route.CompletedAt = DateTime.UtcNow;

        // Buscamos todas las órdenes de la ruta que estén en InRoute
        var linkedOrders = await _db.Orders
            .Where(o => o.DeliveryRouteId == id && o.Status == Models.OrderStatus.InRoute)
            .ToListAsync();

        // Las marcamos como Delivered (o el estado final que uses)
        foreach (var order in linkedOrders)
        {
            order.Status = Models.OrderStatus.Delivered;
            
            // También actualizamos el delivery correspondiente si existe
            var delivery = await _db.Deliveries.FirstOrDefaultAsync(d => d.OrderId == order.Id);
            if(delivery != null && delivery.Status == DeliveryStatus.Pending)
            {
                delivery.Status = DeliveryStatus.Delivered;
                delivery.DeliveredAt = DateTime.UtcNow;
            }
        }

        await _db.SaveChangesAsync();

        return Ok(new { Message = "Ruta liquidada exitosamente." });
    }
}

// DTO auxiliar para el chat
