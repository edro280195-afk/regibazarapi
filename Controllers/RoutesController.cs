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
    private readonly IRouteOptimizerService _optimizer;
    private readonly ILogger<RoutesController> _logger;

    public RoutesController(AppDbContext db, ITokenService tokenService, IConfiguration config, IHubContext<DeliveryHub> hub, IPushNotificationService push, IGeminiService geminiService, IGoogleTtsService tts, IRouteOptimizerService optimizer, ILogger<RoutesController> logger)
    {
        _db = db;
        _tokenService = tokenService;
        _config = config;
        _hub = hub;
        _push = push;
        _geminiService = geminiService;
        _tts = tts;
        _optimizer = optimizer;
        _logger = logger;
    }

    private string FrontendUrl => _config["App:FrontendUrl"] ?? "http://localhost:4200";

    /// <summary>POST /api/routes - Crear ruta con órdenes y/o tandas seleccionadas</summary>
    [HttpPost]
    public async Task<ActionResult<RouteDto>> Create(CreateRouteRequest req)
    {
        var distinctOrderIds = (req.OrderIds ?? new List<int>()).Distinct().ToList();
        var distinctTandaIds = (req.TandaParticipantIds ?? new List<Guid>()).Distinct().ToList();

        if (distinctOrderIds.Count == 0 && distinctTandaIds.Count == 0)
            return BadRequest("Selecciona al menos un pedido o una tanda.");

        var ordersInDb = distinctOrderIds.Count > 0
            ? await _db.Orders
                .Include(o => o.Client)
                .Include(o => o.Delivery)
                .Where(o => distinctOrderIds.Contains(o.Id))
                .ToListAsync()
            : new List<Order>();

        var skippedOrders = ordersInDb
            .Where(o => o.Status == Models.OrderStatus.Canceled
                     || o.Status == Models.OrderStatus.Delivered
                     || o.DeliveryRouteId != null
                     || o.OrderType == OrderType.PickUp)
            .Select(o => new {
                o.Id,
                o.Client.Name,
                Reason = o.Status == Models.OrderStatus.Canceled ? "Cancelado" :
                         o.Status == Models.OrderStatus.Delivered ? "Entregado" :
                         o.DeliveryRouteId != null ? "Ya en otra ruta" : "Es PickUp"
            })
            .ToList();

        // Validación tandas: el participante existe, no fue entregado y no está en otra ruta activa.
        var tandaParticipantsInDb = distinctTandaIds.Count > 0
            ? await _db.TandaParticipants
                .Include(p => p.Client)
                .Include(p => p.Tanda)
                    .ThenInclude(t => t!.Product)
                .Where(p => distinctTandaIds.Contains(p.Id))
                .ToListAsync()
            : new List<TandaParticipant>();

        var tandaParticipantsInActiveRoute = distinctTandaIds.Count > 0
            ? await _db.Deliveries
                .Where(d => d.TandaParticipantId != null
                            && distinctTandaIds.Contains(d.TandaParticipantId!.Value)
                            && (d.DeliveryRoute.Status == RouteStatus.Pending || d.DeliveryRoute.Status == RouteStatus.Active))
                .Select(d => d.TandaParticipantId!.Value)
                .ToListAsync()
            : new List<Guid>();

        var skippedTandas = tandaParticipantsInDb
            .Where(p => p.IsDelivered || tandaParticipantsInActiveRoute.Contains(p.Id))
            .Select(p => new {
                Id = p.Id,
                Name = p.Client?.Name ?? p.CustomerName ?? "Tanda",
                Reason = p.IsDelivered ? "Tanda ya entregada" : "Ya en otra ruta"
            })
            .ToList();

        if ((skippedOrders.Any() || skippedTandas.Any()) && !req.Force)
        {
            return Conflict(new {
                message = "Algunos pedidos o tandas no son aptos para ruta o ya están asignados.",
                skippedOrders,
                skippedTandas
            });
        }

        var orders = ordersInDb
            .Where(o => req.Force || (o.Status != Models.OrderStatus.Canceled
                                     && o.Status != Models.OrderStatus.Delivered
                                     && o.DeliveryRouteId == null
                                     && o.OrderType == OrderType.Delivery))
            .Where(o => o.Status != Models.OrderStatus.Canceled && o.Status != Models.OrderStatus.Delivered)
            .ToList();

        var validTandaParticipants = tandaParticipantsInDb
            .Where(p => req.Force || (!p.IsDelivered && !tandaParticipantsInActiveRoute.Contains(p.Id)))
            .Where(p => !p.IsDelivered) // nunca incluir tandas ya entregadas, ni con force
            .ToList();

        if (!orders.Any() && !validTandaParticipants.Any())
            return BadRequest("No se encontraron pedidos o tandas válidos para ruta.");

        using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var route = new DeliveryRoute
            {
                DriverToken = _tokenService.GenerateAccessToken(),
                Status = RouteStatus.Pending,
                CreatedAt = DateTime.UtcNow,
                Name = $"Ruta {DateTime.Now:dd/MM HH:mm}",
                ScheduledDate = DateTime.SpecifyKind(DateTime.Today, DateTimeKind.Utc)
            };

            _db.DeliveryRoutes.Add(route);

            var sortedOrders = distinctOrderIds
                .Select(id => orders.FirstOrDefault(o => o.Id == id))
                .Where(o => o != null)
                .ToList();

            int sortOrder = 1;
            foreach (var order in sortedOrders!)
            {
                order.DeliveryRoute = route;
                order.Status = Models.OrderStatus.InRoute;

                var delivery = order.Delivery;
                if (delivery == null)
                {
                    delivery = new Delivery
                    {
                        Order = order,
                        Kind = DeliveryKind.Order,
                        DeliveryRoute = route,
                        SortOrder = sortOrder++,
                        Status = DeliveryStatus.Pending
                    };
                    _db.Deliveries.Add(delivery);
                }
                else
                {
                    delivery.DeliveryRoute = route;
                    delivery.Kind = DeliveryKind.Order;
                    delivery.SortOrder = sortOrder++;
                    delivery.Status = DeliveryStatus.Pending;
                }
            }

            // Tandas: una Delivery por cada TandaParticipant. No tiene OrderId.
            var sortedTandas = distinctTandaIds
                .Select(id => validTandaParticipants.FirstOrDefault(p => p.Id == id))
                .Where(p => p != null)
                .ToList();

            foreach (var participant in sortedTandas!)
            {
                _db.Deliveries.Add(new Delivery
                {
                    TandaParticipantId = participant.Id,
                    Kind = DeliveryKind.Tanda,
                    DeliveryRoute = route,
                    SortOrder = sortOrder++,
                    Status = DeliveryStatus.Pending
                });
            }

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();

            // 🔔 Notificaciones Push (después de commit)
            int totalStops = sortedOrders.Count + sortedTandas.Count;
            try
            {
                await _push.NotifyDriversNewRouteAsync(route.Name ?? "Nueva ruta", route.DriverToken, totalStops);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enviando FCM a repartidores");
            }

            foreach (var order in sortedOrders)
            {
                try { await _push.NotifyClientDriverEnRouteAsync(order.ClientId); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error enviando WebPush a cliente {ClientId}", order.ClientId); }
            }

            return Ok(await MapRouteDto(route.Id));
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            
            // Extract detailed inner exception message for debugging
            var innerMsg = ex.InnerException != null ? ex.InnerException.Message : "";
            _logger.LogError(ex, "[RoutesController.Create] ERROR: {Message} | INNER: {Inner}", ex.Message, innerMsg);
            
            return StatusCode(500, new { 
                message = "Error interno al crear la ruta.", 
                detail = ex.Message,
                innerDetail = innerMsg
            });
        }
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
            .Include(r => r.Deliveries).ThenInclude(d => d.Order).ThenInclude(o => o!.Client)
            .Include(r => r.Deliveries).ThenInclude(d => d.Order).ThenInclude(o => o!.Payments)
            .Include(r => r.Deliveries).ThenInclude(d => d.Order).ThenInclude(o => o!.Items)
            .Include(r => r.Deliveries).ThenInclude(d => d.Order).ThenInclude(o => o!.Packages)
            .Include(r => r.Deliveries).ThenInclude(d => d.TandaParticipant).ThenInclude(p => p!.Client)
            .Include(r => r.Deliveries).ThenInclude(d => d.TandaParticipant).ThenInclude(p => p!.Tanda)
                .ThenInclude(t => t!.Product)
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
            Deliveries: route.Deliveries.OrderBy(d => d.SortOrder).Select(MapDeliveryToDto).ToList(),
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

    /// <summary>
    /// Mapea una Delivery a su DTO, soportando tanto pedidos regulares como entregas de tanda.
    /// </summary>
    private RouteDeliveryDto MapDeliveryToDto(Delivery d)
    {
        if (d.Kind == DeliveryKind.Tanda && d.TandaParticipant != null)
        {
            var participant = d.TandaParticipant;
            var tanda = participant.Tanda;
            var client = participant.Client;

            // Semana actual de la tanda (mismo cálculo que TandaService.CalculateCurrentWeek)
            int? currentWeek = null;
            if (tanda != null)
            {
                var days = (int)(DateTime.UtcNow.Date - tanda.StartDate.Date).TotalDays;
                currentWeek = days <= 0 ? 1 : ((days - 1) / 7) + 1;
            }

            return new RouteDeliveryDto(
                DeliveryId: d.Id,
                OrderId: null,
                SortOrder: d.SortOrder,
                ClientName: client?.Name ?? participant.CustomerName ?? "Tanda",
                ClientAddress: client?.Address,
                Latitude: client?.Latitude,
                Longitude: client?.Longitude,
                Status: d.Status.ToString(),
                Total: 0m,
                DeliveredAt: d.DeliveredAt,
                Notes: d.Notes,
                FailureReason: d.FailureReason,
                EvidenceUrls: d.Evidences.Select(e => e.ImagePath).ToList(),
                ClientPhone: client?.Phone,
                PaymentMethod: null,
                Payments: new List<OrderPaymentDto>(),
                Items: new List<OrderItemDto>(),
                AmountPaid: 0m,
                BalanceDue: 0m,
                DeliveryInstructions: client?.DeliveryInstructions,
                ArrivedAt: d.ArrivedAt,
                Packages: new List<OrderPackageDto>(),
                AlternativeAddress: null,
                ClientTag: client?.Tag.ToString(),
                ClientType: client?.Type,
                Kind: "Tanda",
                TandaParticipantId: participant.Id,
                TandaId: participant.TandaId,
                TandaName: tanda?.Name,
                TandaProductName: tanda?.Product?.Name,
                TandaWeek: currentWeek,
                TandaTotalWeeks: tanda?.TotalWeeks,
                TandaVariant: participant.Variant
            );
        }

        // Default: pedido regular (Order)
        var order = d.Order!;
        return new RouteDeliveryDto(
            DeliveryId: d.Id,
            OrderId: d.OrderId,
            SortOrder: d.SortOrder,
            ClientName: order.Client.Name,
            ClientAddress: order.Client.Address,
            Latitude: order.Client.Latitude,
            Longitude: order.Client.Longitude,
            Status: d.Status.ToString(),
            Total: order.Total,
            DeliveredAt: d.DeliveredAt,
            Notes: d.Notes,
            FailureReason: d.FailureReason,
            EvidenceUrls: d.Evidences.Select(e => e.ImagePath).ToList(),
            ClientPhone: order.Client.Phone,
            PaymentMethod: order.PaymentMethod,
            Payments: (order.Payments ?? new List<OrderPayment>())
                .Select(p => new OrderPaymentDto(p.Id, p.OrderId, p.Amount, p.Method, p.Date, p.RegisteredBy, p.Notes)).ToList(),
            Items: (order.Items ?? new List<OrderItem>())
                .Select(i => new OrderItemDto(i.Id, i.ProductName, i.Quantity, i.UnitPrice, i.LineTotal)).ToList(),
            AmountPaid: order.AmountPaid,
            BalanceDue: order.BalanceDue,
            DeliveryInstructions: order.Client.DeliveryInstructions,
            ArrivedAt: d.ArrivedAt,
            Packages: order.Packages.Select(p => new OrderPackageDto(p.Id, p.PackageNumber, p.QrCodeValue, p.Status.ToString(), p.CreatedAt, p.LoadedAt, p.DeliveredAt, p.ReturnedAt)).ToList(),
            AlternativeAddress: order.AlternativeAddress,
            ClientTag: order.Client.Tag.ToString(),
            ClientType: order.Client.Type,
            Kind: "Order"
        );
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

    [HttpGet("{id}/deliveries/{deliveryId}/chat")]
    public async Task<IActionResult> GetDeliveryChat(int id, int deliveryId)
    {
        var msgs = await _db.ChatMessages
            .Where(m => m.DeliveryRouteId == id && m.DeliveryId == deliveryId)
            .OrderBy(m => m.Timestamp)
            .Select(m => new {
                id = m.Id,
                sender = m.Sender,
                text = m.Text,
                timestamp = m.Timestamp,
                deliveryRouteId = m.DeliveryRouteId,
                deliveryId = m.DeliveryId
            })
            .ToListAsync();

        return Ok(msgs);
    }

    [HttpPost("{id}/deliveries/{deliveryId}/chat")]
    public async Task<IActionResult> SendAdminDeliveryMessage(int id, int deliveryId, [FromBody] SendMessageRequest req)
    {
        var route = await _db.DeliveryRoutes.FindAsync(id);
        if (route == null) return NotFound("Ruta no encontrada");

        var delivery = await _db.Deliveries.Include(d => d.Order)
            .FirstOrDefaultAsync(d => d.Id == deliveryId && d.DeliveryRouteId == id);

        if (delivery == null) return NotFound("Entrega no encontrada");

        // Las entregas de tanda no tienen canal público de chat con la clienta.
        if (delivery.Order == null)
            return BadRequest("Esta entrega es una tanda y no admite chat con la clienta.");

        var msg = new ChatMessage
        {
            DeliveryRouteId = route.Id,
            DeliveryId = delivery.Id,
            Sender = "Admin",
            Text = req.Text,
            Timestamp = DateTime.UtcNow
        };

        _db.ChatMessages.Add(msg);
        await _db.SaveChangesAsync();

        var msgDto = new
        {
            id = msg.Id,
            sender = msg.Sender,
            text = msg.Text,
            timestamp = msg.Timestamp,
            deliveryRouteId = msg.DeliveryRouteId,
            deliveryId = msg.DeliveryId
        };

        // Avisamos a la clienta
        await _hub.Clients.Group($"Order_{delivery.Order.AccessToken}")
            .SendAsync("ReceiveClientChatMessage", msgDto);

        // Avisamos al chofer
        await _hub.Clients.Group($"Route_{route.DriverToken}")
            .SendAsync("ReceiveClientChatMessage", msgDto);

        // Avisamos a los otros admins
        await _hub.Clients.Group("Admins")
            .SendAsync("ReceiveClientChatMessage", msgDto);

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

        // 🔔 FCM broadcast al repartidor
        await _push.BroadcastToAllDriversAsync("🔄 Ruta reordenada", $"El orden de entregas de {route.Name} fue actualizado.");

        return Ok(new { Message = "Orden actualizado correctamente" });
    }

    // ═══════════════════════════════════════════
    //  MUTACIÓN ATÓMICA DE RUTA 🛠️
    // ═══════════════════════════════════════════

    [HttpPost("{id}/add-order")]
    public async Task<IActionResult> AddOrderToRoute(int id, [FromBody] int orderId, [FromQuery] double? lat = null, [FromQuery] double? lng = null)
    {
        var route = await _db.DeliveryRoutes.Include(r => r.Deliveries).FirstOrDefaultAsync(r => r.Id == id);
        if (route == null) return NotFound("Ruta no encontrada");

        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
        if (order == null) return NotFound("Orden no encontrada");

        if (order.DeliveryRouteId != null) return BadRequest("La orden ya tiene una ruta asignada.");

        order.DeliveryRouteId = id;
        order.Status = Models.OrderStatus.InRoute;

        // ✨ CRITICAL FIX: Create the Delivery record that was missing
        var delivery = new Delivery
        {
            OrderId = orderId,
            Kind = DeliveryKind.Order,
            DeliveryRouteId = id,
            SortOrder = route.Deliveries.Count + 1,
            Status = DeliveryStatus.Pending
        };
        _db.Deliveries.Add(delivery);
        await _db.SaveChangesAsync();

        if (!string.IsNullOrEmpty(route.DriverToken))
        {
            await _push.NotifyDriverFcmAsync(route.DriverToken, route.Name ?? "Ruta actualizada", $"Se agregaron entregas. Nueva cuenta: {route.Deliveries.Count + 1}", new Dictionary<string, string> { { "action", "REFRESH_ROUTE" } });
            // 📡 Silent refresh via SignalR
            await _hub.Clients.Group($"Route_{route.DriverToken}").SendAsync("RouteUpdated", new { id = route.Id });
        }

        // 🚀 MEJORA: Recalcular la ruta automáticamente para que no se agregue al final
        await OptimizeRouteInternal(id, lat, lng);

        return Ok(new { Message = "Orden agregada y ruta optimizada correctamente" });
    }

    /// <summary>POST /api/routes/{id}/add-tanda - Añade un TandaParticipant a la ruta como entrega.</summary>
    [HttpPost("{id}/add-tanda")]
    public async Task<IActionResult> AddTandaParticipantToRoute(int id, [FromBody] Guid tandaParticipantId, [FromQuery] double? lat = null, [FromQuery] double? lng = null)
    {
        var route = await _db.DeliveryRoutes.Include(r => r.Deliveries).FirstOrDefaultAsync(r => r.Id == id);
        if (route == null) return NotFound("Ruta no encontrada");

        var participant = await _db.TandaParticipants
            .Include(p => p.Client)
            .FirstOrDefaultAsync(p => p.Id == tandaParticipantId);
        if (participant == null) return NotFound("Participante de tanda no encontrado");

        if (participant.IsDelivered) return BadRequest("La tanda ya fue entregada.");

        var alreadyInActiveRoute = await _db.Deliveries.AnyAsync(d =>
            d.TandaParticipantId == tandaParticipantId
            && (d.DeliveryRoute.Status == RouteStatus.Pending || d.DeliveryRoute.Status == RouteStatus.Active));
        if (alreadyInActiveRoute) return BadRequest("La tanda ya está asignada a otra ruta activa.");

        var delivery = new Delivery
        {
            TandaParticipantId = tandaParticipantId,
            Kind = DeliveryKind.Tanda,
            DeliveryRouteId = id,
            SortOrder = route.Deliveries.Count + 1,
            Status = DeliveryStatus.Pending
        };
        _db.Deliveries.Add(delivery);
        await _db.SaveChangesAsync();

        if (!string.IsNullOrEmpty(route.DriverToken))
        {
            await _push.NotifyDriverFcmAsync(route.DriverToken, route.Name ?? "Ruta actualizada",
                $"Se agregó una tanda. Nueva cuenta: {route.Deliveries.Count + 1}",
                new Dictionary<string, string> { { "action", "REFRESH_ROUTE" } });
            await _hub.Clients.Group($"Route_{route.DriverToken}").SendAsync("RouteUpdated", new { id = route.Id });
        }

        await OptimizeRouteInternal(id, lat, lng);

        return Ok(new { Message = "Tanda agregada y ruta optimizada correctamente" });
    }

    [HttpPost("{id}/optimize")]
    public async Task<IActionResult> OptimizeRoute(int id, [FromQuery] double? lat = null, [FromQuery] double? lng = null)
    {
        await OptimizeRouteInternal(id, lat, lng);
        return Ok(new { Message = "Ruta optimizada correctamente" });
    }

    private async Task OptimizeRouteInternal(int routeId, double? startLat = null, double? startLng = null)
    {
        var route = await _db.DeliveryRoutes
            .Include(r => r.Deliveries)
                .ThenInclude(d => d.Order)
                    .ThenInclude(o => o!.Client)
            .Include(r => r.Deliveries)
                .ThenInclude(d => d.TandaParticipant)
                    .ThenInclude(p => p!.Client)
            .FirstOrDefaultAsync(r => r.Id == routeId);

        if (route == null) return;

        // 🛠️ AUTO-REPAIR: Add missing Deliveries for orders in this route
        var ordersInRoute = await _db.Orders
            .Where(o => o.DeliveryRouteId == routeId)
            .ToListAsync();

        bool fixedAny = false;
        foreach (var order in ordersInRoute)
        {
            if (!route.Deliveries.Any(d => d.OrderId == order.Id))
            {
                var newDelivery = new Delivery
                {
                    OrderId = order.Id,
                    Kind = DeliveryKind.Order,
                    DeliveryRouteId = routeId,
                    SortOrder = route.Deliveries.Count + 1,
                    Status = DeliveryStatus.Pending
                };
                _db.Deliveries.Add(newDelivery);
                route.Deliveries.Add(newDelivery);
                fixedAny = true;
            }
        }
        if (fixedAny) await _db.SaveChangesAsync();

        if (!route.Deliveries.Any()) return;
        if (route.Status == RouteStatus.Completed) return;

        var lat = startLat ?? _config.GetValue<double>("Cami:RouteCenterLat", 27.4861);
        var lng = startLng ?? _config.GetValue<double>("Cami:RouteCenterLng", -99.5069);

        // Resolvemos lat/lng de cada delivery (desde Order.Client o TandaParticipant.Client).
        var resolved = route.Deliveries.Select(d =>
        {
            var client = d.Kind == DeliveryKind.Tanda
                ? d.TandaParticipant?.Client
                : d.Order?.Client;
            return new { Delivery = d, Lat = client?.Latitude, Lng = client?.Longitude };
        }).ToList();

        var withCoords = resolved.Where(r => r.Lat != null && r.Lng != null).ToList();
        var withoutCoords = resolved.Where(r => r.Lat == null || r.Lng == null).ToList();

        var ordered = new List<Delivery>();
        var remaining = withCoords.ToList();
        double currentLat = lat;
        double currentLng = lng;

        while (remaining.Any())
        {
            var nearest = remaining
                .Select(r => new { r, dist = HaversineKm(currentLat, currentLng, r.Lat!.Value, r.Lng!.Value) })
                .OrderBy(x => x.dist)
                .First();
            ordered.Add(nearest.r.Delivery);
            currentLat = nearest.r.Lat!.Value;
            currentLng = nearest.r.Lng!.Value;
            remaining.Remove(nearest.r);
        }
        ordered.AddRange(withoutCoords.Select(r => r.Delivery));

        for (int i = 0; i < ordered.Count; i++)
            ordered[i].SortOrder = i + 1;

        await _db.SaveChangesAsync();

        await _hub.Clients.Group($"Route_{route.DriverToken}").SendAsync("RouteUpdated", new { id = route.Id });
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371;
        double dLat = Math.PI * (lat2 - lat1) / 180.0;
        double dLon = Math.PI * (lon2 - lon1) / 180.0;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(Math.PI * lat1 / 180.0) * Math.Cos(Math.PI * lat2 / 180.0) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
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
        await _hub.Clients.Group($"Route_{route.DriverToken}").SendAsync("RouteUpdated", new { id = route.Id });

        // 🔔 FCM broadcast al repartidor
        await _push.BroadcastToAllDriversAsync("📦 Pedido eliminado de ruta", $"Se eliminó un pedido de {route.Name}.", new Dictionary<string, string> { { "action", "REFRESH_ROUTE" } });

        return Ok(new { Message = "Orden eliminada de la ruta correctamente" });
    }

    [HttpDelete("{id}/remove-tanda/{tandaParticipantId}")]
    public async Task<IActionResult> RemoveTandaFromRoute(int id, Guid tandaParticipantId)
    {
        var route = await _db.DeliveryRoutes.FirstOrDefaultAsync(r => r.Id == id);
        if (route == null) return NotFound("Ruta no encontrada");

        var delivery = await _db.Deliveries.FirstOrDefaultAsync(d =>
            d.DeliveryRouteId == id && d.TandaParticipantId == tandaParticipantId);
        if (delivery == null) return NotFound("Entrega de tanda no encontrada en esta ruta.");

        _db.Deliveries.Remove(delivery);
        await _db.SaveChangesAsync();

        await _hub.Clients.Group($"Route_{route.DriverToken}").SendAsync("RouteUpdated", new { id = route.Id });
        await _push.BroadcastToAllDriversAsync("✨ Tanda eliminada de ruta",
            $"Se eliminó una tanda de {route.Name}.",
            new Dictionary<string, string> { { "action", "REFRESH_ROUTE" } });

        return Ok(new { Message = "Tanda eliminada de la ruta correctamente" });
    }

    /// <summary>GET /api/routes/available-tandas - Lista tandas pendientes para el domingo siguiente.</summary>
    [HttpGet("available-tandas")]
    public async Task<ActionResult<List<AvailableTandaDto>>> GetAvailableTandas()
    {
        var participants = await _db.TandaParticipants
            .Include(p => p.Client)
            .Include(p => p.Tanda)
                .ThenInclude(t => t!.Product)
            .Where(p => !p.IsDelivered && p.Tanda != null && p.Tanda.Status == "Active")
            .ToListAsync();

        // ID de participantes que ya están en una ruta activa
        var assignedIds = await _db.Deliveries
            .Where(d => d.TandaParticipantId != null
                        && (d.DeliveryRoute.Status == RouteStatus.Pending || d.DeliveryRoute.Status == RouteStatus.Active))
            .Select(d => d.TandaParticipantId!.Value)
            .ToListAsync();

        var nowUtc = DateTime.UtcNow.Date;
        var result = participants
            .Where(p => !assignedIds.Contains(p.Id))
            .Where(p =>
            {
                // Sólo participantes cuyo turno es el actual o pasado de la tanda activa.
                var startDate = p.Tanda!.StartDate.Date;
                var days = (int)(nowUtc - startDate).TotalDays;
                int currentWeek = days <= 0 ? 1 : ((days - 1) / 7) + 1;
                return p.AssignedTurn <= currentWeek;
            })
            .Select(p =>
            {
                var startDate = p.Tanda!.StartDate.Date;
                var days = (int)(nowUtc - startDate).TotalDays;
                int currentWeek = days <= 0 ? 1 : ((days - 1) / 7) + 1;
                return new AvailableTandaDto(
                    TandaParticipantId: p.Id,
                    TandaId: p.TandaId,
                    TandaName: p.Tanda!.Name,
                    TandaProductName: p.Tanda.Product?.Name,
                    Week: currentWeek,
                    TotalWeeks: p.Tanda.TotalWeeks,
                    Variant: p.Variant,
                    ClientId: p.CustomerId,
                    ClientName: p.Client?.Name ?? p.CustomerName ?? "Tanda",
                    ClientAddress: p.Client?.Address,
                    ClientPhone: p.Client?.Phone,
                    ClientLatitude: p.Client?.Latitude,
                    ClientLongitude: p.Client?.Longitude,
                    DeliveryInstructions: p.Client?.DeliveryInstructions
                );
            })
            .OrderBy(t => t.TandaName)
            .ThenBy(t => t.ClientName)
            .ToList();

        return Ok(result);
    }

    // ═══════════════════════════════════════════
    //  HELPERS & DELETE
    // ═══════════════════════════════════════════

    private async Task<RouteDto> MapRouteDto(int routeId)
    {
        var route = await _db.DeliveryRoutes
            .FirstAsync(r => r.Id == routeId);

        var deliveries = await _db.Deliveries
            .Include(d => d.Order).ThenInclude(o => o!.Client)
            .Include(d => d.Order).ThenInclude(o => o!.Payments)
            .Include(d => d.Order).ThenInclude(o => o!.Items)
            .Include(d => d.Order).ThenInclude(o => o!.Packages)
            .Include(d => d.TandaParticipant).ThenInclude(p => p!.Client)
            .Include(d => d.TandaParticipant).ThenInclude(p => p!.Tanda).ThenInclude(t => t!.Product)
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
            Deliveries: deliveries.Select(MapDeliveryToDto).ToList(),
            Expenses: expenses
        );
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var route = await _db.DeliveryRoutes
                .Include(r => r.Deliveries)
                .Include(r => r.ChatMessages)
                .Include(r => r.Orders)
                .FirstOrDefaultAsync(r => r.Id == id);

            if (route == null) return NotFound(new { message = "Ruta no encontrada" });

            string routeName = route.Name ?? $"Ruta #{id}";
            string driverToken = route.DriverToken;

            // 1. Liberar los pedidos asociados
            var linkedOrders = await _db.Orders
                .Where(o => o.DeliveryRouteId == id)
                .ToListAsync();

            foreach (var order in linkedOrders)
            {
                order.DeliveryRouteId = null;
                // Si la orden estaba en ruta, la regresamos a pendiente
                if (order.Status == OrderStatus.InRoute)
                {
                    order.Status = OrderStatus.Pending;
                }
            }

            // 2. Obtener IDs de entregas para limpiar dependencias
            var deliveryIds = route.Deliveries.Select(d => d.Id).ToList();

            // 3. Borrar chats asociados a la ruta o a sus entregas
            var chats = await _db.ChatMessages
                .Where(c => c.DeliveryRouteId == id || (c.DeliveryId != null && deliveryIds.Contains(c.DeliveryId.Value)))
                .ToListAsync();
            if (chats.Any()) _db.ChatMessages.RemoveRange(chats);

            // 4. Borrar gastos del chofer asociados a esta ruta
            var expenses = await _db.DriverExpenses.Where(e => e.DeliveryRouteId == id).ToListAsync();
            if (expenses.Any()) _db.DriverExpenses.RemoveRange(expenses);

            // 5. Borrar evidencias de entrega
            var evidences = await _db.DeliveryEvidences.Where(e => deliveryIds.Contains(e.DeliveryId)).ToListAsync();
            if (evidences.Any()) _db.DeliveryEvidences.RemoveRange(evidences);

            // 6. Borrar las entregas (junction records)
            if (route.Deliveries.Any()) _db.Deliveries.RemoveRange(route.Deliveries);

            // 7. Borrar la ruta
            _db.DeliveryRoutes.Remove(route);

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();

            // 🔔 Notificaciones Push/SignalR
            try
            {
                await _hub.Clients.Group($"Route_{driverToken}").SendAsync("RouteDeleted", new
                {
                    Message = $"La ruta '{routeName}' fue eliminada por el administrador."
                });
                await _push.BroadcastToAllDriversAsync("🚫 Ruta cancelada", $"La ruta {routeName} fue eliminada.");
            }
            catch { /* Ignorar errores de notificación */ }

            return Ok(new { message = "Ruta eliminada correctamente y pedidos liberados." });
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            return BadRequest(new { message = $"Error al eliminar la ruta: {ex.Message}" });
        }
    }

    // ═══════════════════════════════════════════
    //  LIQUIDACIÓN DE RUTA (CORTE DE CAJA)
    // ═══════════════════════════════════════════
    [HttpPost("{id}/liquidate")]
    public async Task<IActionResult> LiquidateRoute(int id)
    {
        var route = await _db.DeliveryRoutes.FindAsync(id);
        if (route == null) return NotFound("Ruta no encontrada");

        route.Status = RouteStatus.Completed;
        route.CompletedAt = DateTime.UtcNow;

        var linkedOrders = await _db.Orders
            .Where(o => o.DeliveryRouteId == id && o.Status == Models.OrderStatus.InRoute)
            .ToListAsync();

        foreach (var order in linkedOrders)
        {
            order.Status = Models.OrderStatus.Delivered;

            var delivery = await _db.Deliveries.FirstOrDefaultAsync(d => d.OrderId == order.Id);
            if (delivery != null && delivery.Status == DeliveryStatus.Pending)
            {
                delivery.Status = DeliveryStatus.Delivered;
                delivery.DeliveredAt = DateTime.UtcNow;
            }
        }

        // Tandas en la ruta: marcamos las pendientes como entregadas y propagamos al TandaParticipant.
        var tandaDeliveries = await _db.Deliveries
            .Include(d => d.TandaParticipant)
            .Where(d => d.DeliveryRouteId == id
                        && d.Kind == DeliveryKind.Tanda
                        && d.Status == DeliveryStatus.Pending)
            .ToListAsync();

        var now = DateTime.UtcNow;
        foreach (var d in tandaDeliveries)
        {
            d.Status = DeliveryStatus.Delivered;
            d.DeliveredAt = now;
            if (d.TandaParticipant != null)
            {
                d.TandaParticipant.IsDelivered = true;
                d.TandaParticipant.DeliveryDate = now;
            }
        }

        await _db.SaveChangesAsync();

        return Ok(new { Message = "Ruta liquidada exitosamente." });
    }
}

// DTO auxiliar para el chat
