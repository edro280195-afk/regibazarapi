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
    private readonly IElevenLabsTtsService _tts;
    private readonly IRouteOptimizerService _optimizer;
    private readonly ILogger<RoutesController> _logger;

    public RoutesController(AppDbContext db, ITokenService tokenService, IConfiguration config, IHubContext<DeliveryHub> hub, IPushNotificationService push, IGeminiService geminiService, IElevenLabsTtsService tts, IRouteOptimizerService optimizer, ILogger<RoutesController> logger)
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

    /// <summary>POST /api/routes - Crear ruta con órdenes y/o tandas seleccionadas. Optimiza localmente por coordenadas.</summary>
    [HttpPost]
    public async Task<ActionResult<CreateRouteResponse>> Create(CreateRouteRequest req)
    {
        var distinctOrderIds = (req.OrderIds ?? new List<int>()).Distinct().ToList();
        var distinctTandaIds = (req.TandaParticipantIds ?? new List<Guid>()).Distinct().ToList();

        if (distinctOrderIds.Count == 0 && distinctTandaIds.Count == 0)
            return BadRequest("Selecciona al menos un pedido o una tanda.");

        var (orders, tandas, skipped) = await ValidateAndPartitionAsync(distinctOrderIds, distinctTandaIds);

        if (orders.Count == 0 && tandas.Count == 0)
        {
            return BadRequest(new
            {
                message = "Ningún pedido ni tanda es válido para esta ruta.",
                skipped
            });
        }

        // 🛍️ Puerta de bolsas: no dejamos armar la ruta si algún pedido no tiene bolsas
        // confirmadas. La dueña debe resolverlo (poner número o "va sin bolsas") o forzar
        // con req.Force. Así el dato se captura justo cuando importa: al cargar la camioneta.
        if (!req.Force)
        {
            var sinBolsas = orders.Where(o => !o.PackagesConfirmed).ToList();
            if (sinBolsas.Count > 0)
            {
                return BadRequest(new
                {
                    code = "PACKAGES_REQUIRED",
                    message = sinBolsas.Count == 1
                        ? $"El pedido de {sinBolsas[0].Client?.Name ?? "esta clienta"} no tiene bolsas capturadas. Agrégalas antes de mandarlo a ruta 🛍️"
                        : $"{sinBolsas.Count} pedidos no tienen bolsas capturadas. Agrégalas antes de mandarlos a ruta 🛍️",
                    orders = sinBolsas.Select(o => new
                    {
                        id = o.Id,
                        clientName = o.Client?.Name ?? $"#{o.Id}",
                        totalPackages = o.TotalPackages
                    }).ToList()
                });
            }
        }

        // Resolver coordenadas: stops para el optimizer
        var allStops = new List<RouteStop>();
        foreach (var o in orders)
            allStops.Add(new RouteStop($"order:{o.Id}", o.Client?.Latitude, o.Client?.Longitude));
        foreach (var p in tandas)
            allStops.Add(new RouteStop($"tanda:{p.Id}", p.Client?.Latitude, p.Client?.Longitude));

        // Si el frontend ya optimizó, respetamos su orden. Si no, llamamos a Google Routes.
        List<string> orderedIds;
        if (req.PreOptimized)
        {
            // El orden recibido en req.OrderIds + req.TandaParticipantIds es el deseado.
            orderedIds = distinctOrderIds.Select(i => $"order:{i}")
                .Concat(distinctTandaIds.Select(g => $"tanda:{g}"))
                .Where(id => allStops.Any(s => s.Id == id))
                .ToList();
        }
        else
        {
            var center = (
                lat: _config.GetValue<double>("Cami:RouteCenterLat", 27.4861),
                lng: _config.GetValue<double>("Cami:RouteCenterLng", -99.5069)
            );
            var optimized = await _optimizer.OptimizeAsync(allStops, center.lat, center.lng);
            orderedIds = optimized.OrderedStopIds;
        }

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

            int sortOrder = 1;
            var createdOrderClientIds = new List<int>();

            foreach (var stopId in orderedIds)
            {
                if (stopId.StartsWith("order:"))
                {
                    var orderId = int.Parse(stopId.Substring(6));
                    var order = orders.FirstOrDefault(o => o.Id == orderId);
                    if (order == null) continue;

                    _db.Deliveries.Add(DeliveryRetryPolicy.CreateForRoute(order, route, sortOrder++));
                    createdOrderClientIds.Add(order.ClientId);
                }
                else if (stopId.StartsWith("tanda:"))
                {
                    var tandaId = Guid.Parse(stopId.Substring(6));
                    var participant = tandas.FirstOrDefault(p => p.Id == tandaId);
                    if (participant == null) continue;

                    _db.Deliveries.Add(new Delivery
                    {
                        TandaParticipantId = participant.Id,
                        Kind = DeliveryKind.Tanda,
                        DeliveryRoute = route,
                        SortOrder = sortOrder++,
                        Status = DeliveryStatus.Pending
                    });
                }
            }

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();

            // 🔔 Notificaciones Push (después de commit)
            int totalStops = sortOrder - 1;
            try { await _push.NotifyDriversNewRouteAsync(route.Name ?? "Nueva ruta", route.DriverToken, totalStops); }
            catch (Exception ex) { _logger.LogError(ex, "Error enviando FCM a repartidores"); }

            foreach (var clientId in createdOrderClientIds.Distinct())
            {
                try { await _push.NotifyClientDriverEnRouteAsync(clientId); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error enviando WebPush a cliente {ClientId}", clientId); }
            }

            var routeDto = await MapRouteDto(route.Id);
            return Ok(new CreateRouteResponse(routeDto, skipped));
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            var innerMsg = ex.InnerException != null ? ex.InnerException.Message : "";
            _logger.LogError(ex, "[RoutesController.Create] ERROR: {Message} | INNER: {Inner}", ex.Message, innerMsg);
            return StatusCode(500, new {
                message = "Error interno al crear la ruta.",
                detail = ex.Message,
                innerDetail = innerMsg
            });
        }
    }

    /// <summary>POST /api/routes/preview - Calcula ruta óptima sin guardar. Usado por el builder.</summary>
    [HttpPost("preview")]
    public async Task<ActionResult<PreviewRouteResponse>> PreviewRoute([FromBody] PreviewRouteRequest req)
    {
        var orderIds = (req.OrderIds ?? new List<int>()).Distinct().ToList();
        var tandaIds = (req.TandaParticipantIds ?? new List<Guid>()).Distinct().ToList();

        if (orderIds.Count == 0 && tandaIds.Count == 0)
            return Ok(new PreviewRouteResponse(new List<PreviewStopDto>(), 0, 0, "empty", new List<SkippedStopDto>(), 0));

        var (orders, tandas, skipped) = await ValidateAndPartitionAsync(orderIds, tandaIds);

        var allStops = new List<RouteStop>();
        foreach (var o in orders)
            allStops.Add(new RouteStop($"order:{o.Id}", o.Client?.Latitude, o.Client?.Longitude));
        foreach (var p in tandas)
            allStops.Add(new RouteStop($"tanda:{p.Id}", p.Client?.Latitude, p.Client?.Longitude));

        var centerLat = req.StartLat ?? _config.GetValue<double>("Cami:RouteCenterLat", 27.4861);
        var centerLng = req.StartLng ?? _config.GetValue<double>("Cami:RouteCenterLng", -99.5069);

        var optimized = await _optimizer.OptimizeAsync(allStops, centerLat, centerLng);

        var stopsDto = new List<PreviewStopDto>();
        int idx = 1;
        foreach (var stopId in optimized.OrderedStopIds)
        {
            if (stopId.StartsWith("order:"))
            {
                var orderId = int.Parse(stopId.Substring(6));
                var o = orders.FirstOrDefault(x => x.Id == orderId);
                if (o == null) continue;
                stopsDto.Add(new PreviewStopDto(
                    Kind: "Order",
                    OrderId: o.Id,
                    TandaParticipantId: null,
                    SortOrder: idx++,
                    ClientName: o.Client?.Name ?? "Cliente",
                    ClientAddress: ClientDataPolicy.ResolveDeliveryAddress(
                        o.Client?.Address,
                        o.AlternativeAddress),
                    Latitude: o.Client?.Latitude,
                    Longitude: o.Client?.Longitude,
                    Total: o.Total,
                    HasCoords: o.Client?.Latitude.HasValue == true && o.Client?.Longitude.HasValue == true,
                    TandaName: null,
                    TandaWeek: null
                ));
            }
            else if (stopId.StartsWith("tanda:"))
            {
                var tandaId = Guid.Parse(stopId.Substring(6));
                var p = tandas.FirstOrDefault(x => x.Id == tandaId);
                if (p == null) continue;
                int? currentWeek = null;
                if (p.Tanda != null)
                {
                    var days = (int)(DateTime.UtcNow.Date - p.Tanda.StartDate.Date).TotalDays;
                    currentWeek = days <= 0 ? 1 : ((days - 1) / 7) + 1;
                }
                stopsDto.Add(new PreviewStopDto(
                    Kind: "Tanda",
                    OrderId: null,
                    TandaParticipantId: p.Id,
                    SortOrder: idx++,
                    ClientName: p.Client?.Name ?? p.CustomerName ?? "Tanda",
                    ClientAddress: p.Client?.Address,
                    Latitude: p.Client?.Latitude,
                    Longitude: p.Client?.Longitude,
                    Total: 0m,
                    HasCoords: p.Client?.Latitude.HasValue == true && p.Client?.Longitude.HasValue == true,
                    TandaName: p.Tanda?.Name,
                    TandaWeek: currentWeek
                ));
            }
        }

        int withoutCoords = stopsDto.Count(s => !s.HasCoords);
        return Ok(new PreviewRouteResponse(
            Stops: stopsDto,
            TotalDistanceMeters: optimized.DistanceMeters,
            TotalDurationSeconds: optimized.DurationSeconds,
            OptimizerSource: optimized.Source,
            Skipped: skipped,
            StopsWithoutCoords: withoutCoords,
            PolylineEncoded: optimized.PolylineEncoded,
            DepotLatitude: centerLat,
            DepotLongitude: centerLng
        ));
    }

    /// <summary>
    /// Valida los IDs recibidos y los separa entre los aptos (devueltos como entidades cargadas)
    /// y los rechazados (devueltos con razón específica).
    /// </summary>
    private async Task<(List<Order> orders, List<TandaParticipant> tandas, List<SkippedStopDto> skipped)>
        ValidateAndPartitionAsync(List<int> orderIds, List<Guid> tandaIds)
    {
        var skipped = new List<SkippedStopDto>();

        var ordersInDb = orderIds.Count > 0
            ? await _db.Orders
                .Include(o => o.Client)
                .Include(o => o.Packages)
                .Where(o => orderIds.Contains(o.Id))
                .ToListAsync()
            : new List<Order>();

        // IDs faltantes (no existen en BD)
        foreach (var id in orderIds.Where(id => ordersInDb.All(o => o.Id != id)))
            skipped.Add(new SkippedStopDto("Order", id.ToString(), $"Pedido #{id}", "No existe"));

        var validOrders = new List<Order>();
        foreach (var o in ordersInDb)
        {
            string? reason = null;
            if (o.Status == Models.OrderStatus.Canceled) reason = "Cancelado";
            else if (o.Status == Models.OrderStatus.Delivered) reason = "Ya entregado";
            else if (o.DeliveryRouteId != null) reason = "Ya en otra ruta";
            else if (o.OrderType == OrderType.PickUp) reason = "Es pickup, no delivery";
            else if (o.Packages.Any(p => p.Status == PackageTrackingStatus.Loaded)) reason = "Sus bolsas siguen cargadas en una ruta anterior";

            if (reason != null)
                skipped.Add(new SkippedStopDto("Order", o.Id.ToString(), o.Client?.Name ?? $"#{o.Id}", reason));
            else
                validOrders.Add(o);
        }

        var tandasInDb = tandaIds.Count > 0
            ? await _db.TandaParticipants
                .Include(p => p.Client)
                .Include(p => p.Tanda)
                    .ThenInclude(t => t!.Product)
                .Where(p => tandaIds.Contains(p.Id))
                .ToListAsync()
            : new List<TandaParticipant>();

        foreach (var id in tandaIds.Where(id => tandasInDb.All(p => p.Id != id)))
            skipped.Add(new SkippedStopDto("Tanda", id.ToString(), "Tanda", "No existe"));

        var tandasInActiveRoute = tandaIds.Count > 0
            ? await _db.Deliveries
                .Where(d => d.TandaParticipantId != null
                            && tandaIds.Contains(d.TandaParticipantId!.Value)
                            && (d.DeliveryRoute.Status == RouteStatus.Pending || d.DeliveryRoute.Status == RouteStatus.Active))
                .Select(d => d.TandaParticipantId!.Value)
                .ToListAsync()
            : new List<Guid>();

        var validTandas = new List<TandaParticipant>();
        foreach (var p in tandasInDb)
        {
            string? reason = null;
            if (p.IsDelivered) reason = "Tanda ya entregada";
            else if (tandasInActiveRoute.Contains(p.Id)) reason = "Ya en otra ruta activa";

            if (reason != null)
                skipped.Add(new SkippedStopDto("Tanda", p.Id.ToString(), p.Client?.Name ?? p.CustomerName ?? "Tanda", reason));
            else
                validTandas.Add(p);
        }

        return (validOrders, validTandas, skipped);
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

    [HttpGet("{id}/chat")]
    public async Task<IActionResult> GetRouteChat(int id)
    {
        var msgs = await _db.ChatMessages
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
            Sender = "Admin",
            Text = req.Text,
            Timestamp = DateTime.UtcNow,
            DeliveryId = null
        };

        _db.ChatMessages.Add(msg);
        await _db.SaveChangesAsync();

        var msgDto = new
        {
            id = msg.Id,
            sender = msg.Sender,
            text = msg.Text,
            timestamp = msg.Timestamp,
            deliveryRouteId = msg.DeliveryRouteId
        };

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

        await _hub.Clients.Group($"Order_{delivery.Order.AccessToken}")
            .SendAsync("ReceiveClientChatMessage", msgDto);
        await _hub.Clients.Group($"Route_{route.DriverToken}")
            .SendAsync("ReceiveClientChatMessage", msgDto);
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

        int newOrder = 1;
        foreach (var deliveryId in deliveryIdsInOrder)
        {
            var delivery = route.Deliveries.FirstOrDefault(d => d.Id == deliveryId);
            if (delivery != null) delivery.SortOrder = newOrder++;
        }

        await _db.SaveChangesAsync();

        await _hub.Clients.Group($"Route_{route.DriverToken}").SendAsync("RouteUpdated");
        await _push.BroadcastToAllDriversAsync("🔄 Ruta reordenada", $"El orden de entregas de {route.Name} fue actualizado.");

        return Ok(new { Message = "Orden actualizado correctamente" });
    }

    /// <summary>PATCH /api/routes/{id}/deliveries/{deliveryId}/notes - Edita la nota de una entrega (la ve el chofer en su ruta).</summary>
    [HttpPatch("{id}/deliveries/{deliveryId}/notes")]
    public async Task<IActionResult> UpdateDeliveryNotes(int id, int deliveryId, [FromBody] UpdateDeliveryNotesRequest req)
    {
        var route = await _db.DeliveryRoutes.FirstOrDefaultAsync(r => r.Id == id);
        if (route == null) return NotFound("Ruta no encontrada");

        var delivery = await _db.Deliveries.FirstOrDefaultAsync(d => d.Id == deliveryId && d.DeliveryRouteId == id);
        if (delivery == null) return NotFound("Entrega no encontrada en esta ruta");

        var notes = req.Notes?.Trim();
        if (notes is { Length: > 500 }) return BadRequest("La nota no puede exceder 500 caracteres.");

        delivery.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes;
        await _db.SaveChangesAsync();

        if (!string.IsNullOrEmpty(route.DriverToken))
            await _hub.Clients.Group($"Route_{route.DriverToken}").SendAsync("RouteUpdated", new { id = route.Id });

        return Ok(new { Message = "Nota actualizada", delivery.Notes });
    }

    // ═══════════════════════════════════════════
    //  MUTACIÓN ATÓMICA DE RUTA 🛠️
    // ═══════════════════════════════════════════

    [HttpPost("{id}/add-order")]
    public async Task<IActionResult> AddOrderToRoute(int id, [FromBody] int orderId, [FromQuery] double? lat = null, [FromQuery] double? lng = null)
    {
        var route = await _db.DeliveryRoutes.Include(r => r.Deliveries).FirstOrDefaultAsync(r => r.Id == id);
        if (route == null) return NotFound("Ruta no encontrada");

        var order = await _db.Orders
            .Include(o => o.Client)
            .Include(o => o.Packages)
            .FirstOrDefaultAsync(o => o.Id == orderId);
        if (order == null) return NotFound("Orden no encontrada");

        if (order.DeliveryRouteId != null) return BadRequest("La orden ya tiene una ruta asignada.");
        if (order.Status is Models.OrderStatus.Delivered or Models.OrderStatus.Canceled)
            return BadRequest("La orden no se puede agregar a una ruta.");
        if (order.Packages.Any(p => p.Status == PackageTrackingStatus.Loaded))
            return BadRequest("No se puede agregar: sus bolsas siguen cargadas en una ruta anterior.");

        // 🛍️ Puerta de bolsas también al agregar a una ruta existente.
        if (!order.PackagesConfirmed)
        {
            var clientName = await _db.Clients.Where(c => c.Id == order.ClientId).Select(c => c.Name).FirstOrDefaultAsync();
            return BadRequest(new
            {
                code = "PACKAGES_REQUIRED",
                message = $"El pedido de {clientName ?? "esta clienta"} no tiene bolsas capturadas. Agrégalas antes de mandarlo a ruta 🛍️",
                orders = new[]
                {
                    new { id = order.Id, clientName = clientName ?? $"#{order.Id}", totalPackages = order.TotalPackages }
                }
            });
        }

        _db.Deliveries.Add(DeliveryRetryPolicy.CreateForRoute(order, route, route.Deliveries.Count + 1));
        await _db.SaveChangesAsync();

        if (!string.IsNullOrEmpty(route.DriverToken))
        {
            await _push.NotifyDriverFcmAsync(route.DriverToken, route.Name ?? "Ruta actualizada", $"Se agregaron entregas. Nueva cuenta: {route.Deliveries.Count + 1}", new Dictionary<string, string> { { "action", "REFRESH_ROUTE" } });
            await _hub.Clients.Group($"Route_{route.DriverToken}").SendAsync("RouteUpdated", new { id = route.Id });
        }

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
    public async Task<IActionResult> OptimizeRoute(
        int id,
        [FromQuery] double? lat = null,
        [FromQuery] double? lng = null)
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

        var ordersInRoute = await _db.Orders.Where(o => o.DeliveryRouteId == routeId).ToListAsync();

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

        var deliveryById = route.Deliveries.ToDictionary(d => StopIdFor(d), d => d);
        var stops = route.Deliveries.Select(d =>
        {
            var client = d.Kind == DeliveryKind.Tanda ? d.TandaParticipant?.Client : d.Order?.Client;
            return new RouteStop(StopIdFor(d), client?.Latitude, client?.Longitude);
        }).ToList();

        var optimized = await _optimizer.OptimizeAsync(stops, lat, lng);

        for (int i = 0; i < optimized.OrderedStopIds.Count; i++)
        {
            if (deliveryById.TryGetValue(optimized.OrderedStopIds[i], out var d))
                d.SortOrder = i + 1;
        }

        await _db.SaveChangesAsync();
        await _hub.Clients.Group($"Route_{route.DriverToken}").SendAsync("RouteUpdated", new { id = route.Id });
    }

    private static string StopIdFor(Delivery d) =>
        d.Kind == DeliveryKind.Tanda ? $"tanda:{d.TandaParticipantId}" : $"order:{d.OrderId}";

    [HttpDelete("{id}/remove-order/{orderId}")]
    public async Task<IActionResult> RemoveOrderFromRoute(int id, int orderId)
    {
        var route = await _db.DeliveryRoutes.Include(r => r.Deliveries).FirstOrDefaultAsync(r => r.Id == id);
        if (route == null) return NotFound("Ruta no encontrada");

        var delivery = await _db.Deliveries.FirstOrDefaultAsync(d => d.DeliveryRouteId == id && d.OrderId == orderId);
        if (delivery == null) return NotFound("Entrega no encontrada en esta ruta.");
        if (delivery.Status is DeliveryStatus.Delivered or DeliveryStatus.NotDelivered)
            return BadRequest("No se puede eliminar un intento cerrado porque conserva el historial de entrega.");

        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
        if (order != null) { order.DeliveryRouteId = null; order.Status = Models.OrderStatus.Pending; }

        _db.Deliveries.Remove(delivery);
        await _db.SaveChangesAsync();

        await _hub.Clients.Group($"Route_{route.DriverToken}").SendAsync("RouteUpdated", new { id = route.Id });
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

    /// <summary>GET /api/routes/available-tandas - Lista todas las tandas activas no entregadas ni asignadas.</summary>
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
    //  RE-ARMADO DE RUTA EXISTENTE
    // ═══════════════════════════════════════════

    /// <summary>
    /// PUT /api/routes/{id}/recompose — Rehace las paradas pendientes de una ruta existente.
    /// Las entregas ya Delivered o Failed quedan bloqueadas. Solo se re-optimizan las Pending.
    /// </summary>
    [HttpPut("{id}/recompose")]
    public async Task<ActionResult<RecomposeRouteResponse>> Recompose(int id, [FromBody] RecomposeRouteRequest req)
    {
        var route = await _db.DeliveryRoutes
            .Include(r => r.Deliveries)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (route == null) return NotFound("Ruta no encontrada.");
        if (route.Status == RouteStatus.Completed)
            return BadRequest("La ruta ya está completada y no se puede recomponer.");

        var distinctOrderIds = (req.OrderIds ?? new List<int>()).Distinct().ToList();
        var distinctTandaIds = (req.TandaParticipantIds ?? new List<Guid>()).Distinct().ToList();
        var skipped = new List<SkippedStopDto>();
        var failedOrderIdsInThisRoute = route.Deliveries
            .Where(d => d.Kind == DeliveryKind.Order
                && d.OrderId != null
                && d.Status == DeliveryStatus.NotDelivered)
            .Select(d => d.OrderId!.Value)
            .ToHashSet();

        var ordersInDb = distinctOrderIds.Count > 0
            ? await _db.Orders.Include(o => o.Client)
                .Include(o => o.Packages)
                .Where(o => distinctOrderIds.Contains(o.Id))
                .ToListAsync()
            : new List<Order>();

        foreach (var oid in distinctOrderIds.Where(i => ordersInDb.All(o => o.Id != i)))
            skipped.Add(new SkippedStopDto("Order", oid.ToString(), $"Pedido #{oid}", "No existe"));

        var validOrders = new List<Order>();
        foreach (var o in ordersInDb)
        {
            string? reason = null;
            if (o.Status == Models.OrderStatus.Canceled) reason = "Cancelado";
            else if (o.Status == Models.OrderStatus.Delivered) reason = "Ya entregado";
            else if (o.OrderType == OrderType.PickUp) reason = "Es pickup, no delivery";
            else if (failedOrderIdsInThisRoute.Contains(o.Id)) reason = "Tuvo un intento fallido en esta ruta; reintentalo en una ruta nueva";
            else if (o.DeliveryRouteId != null && o.DeliveryRouteId != id) reason = "En otra ruta";
            else if (o.DeliveryRouteId != id && o.Packages.Any(p => p.Status == PackageTrackingStatus.Loaded)) reason = "Sus bolsas siguen cargadas en una ruta anterior";
            if (reason != null)
                skipped.Add(new SkippedStopDto("Order", o.Id.ToString(), o.Client?.Name ?? $"#{o.Id}", reason));
            else
                validOrders.Add(o);
        }

        var tandasInDb = distinctTandaIds.Count > 0
            ? await _db.TandaParticipants
                .Include(p => p.Client)
                .Include(p => p.Tanda).ThenInclude(t => t!.Product)
                .Where(p => distinctTandaIds.Contains(p.Id))
                .ToListAsync()
            : new List<TandaParticipant>();

        foreach (var tid in distinctTandaIds.Where(g => tandasInDb.All(p => p.Id != g)))
            skipped.Add(new SkippedStopDto("Tanda", tid.ToString(), "Tanda", "No existe"));

        var tandasInOtherRoutes = distinctTandaIds.Count > 0
            ? await _db.Deliveries
                .Where(d => d.TandaParticipantId != null
                            && distinctTandaIds.Contains(d.TandaParticipantId!.Value)
                            && d.DeliveryRouteId != id
                            && (d.DeliveryRoute.Status == RouteStatus.Pending || d.DeliveryRoute.Status == RouteStatus.Active))
                .Select(d => d.TandaParticipantId!.Value)
                .ToListAsync()
            : new List<Guid>();

        var validTandas = new List<TandaParticipant>();
        foreach (var p in tandasInDb)
        {
            string? reason = null;
            if (p.IsDelivered) reason = "Tanda ya entregada";
            else if (tandasInOtherRoutes.Contains(p.Id)) reason = "Ya en otra ruta activa";
            if (reason != null)
                skipped.Add(new SkippedStopDto("Tanda", p.Id.ToString(), p.Client?.Name ?? p.CustomerName ?? "Tanda", reason));
            else
                validTandas.Add(p);
        }

        var existingDeliveries = await _db.Deliveries
            .Include(d => d.Order)
            .Where(d => d.DeliveryRouteId == id)
            .ToListAsync();

        var currentPendingDeliveries = existingDeliveries
            .Where(d => d.Status != DeliveryStatus.Delivered && d.Status != DeliveryStatus.NotDelivered)
            .ToList();

        using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var newOrderIdSet = new HashSet<int>(validOrders.Select(o => o.Id));
            var newTandaIdSet = new HashSet<Guid>(validTandas.Select(p => p.Id));

            foreach (var d in currentPendingDeliveries)
            {
                bool stillWanted = (d.Kind == DeliveryKind.Order && d.OrderId != null && newOrderIdSet.Contains(d.OrderId.Value))
                                || (d.Kind == DeliveryKind.Tanda && d.TandaParticipantId != null && newTandaIdSet.Contains(d.TandaParticipantId.Value));
                if (!stillWanted)
                {
                    if (d.Kind == DeliveryKind.Order && d.Order != null)
                    {
                        d.Order.DeliveryRouteId = null;
                        if (d.Order.Status == Models.OrderStatus.InRoute) d.Order.Status = Models.OrderStatus.Pending;
                    }
                    _db.Deliveries.Remove(d);
                }
            }
            await _db.SaveChangesAsync();

            var existingOrderIds = currentPendingDeliveries
                .Where(d => d.Kind == DeliveryKind.Order && d.OrderId != null)
                .Select(d => d.OrderId!.Value).ToHashSet();
            var existingTandaIds = currentPendingDeliveries
                .Where(d => d.Kind == DeliveryKind.Tanda && d.TandaParticipantId != null)
                .Select(d => d.TandaParticipantId!.Value).ToHashSet();

            foreach (var o in validOrders.Where(o => !existingOrderIds.Contains(o.Id)))
            {
                _db.Deliveries.Add(DeliveryRetryPolicy.CreateForRoute(o, route, 999));
            }
            foreach (var p in validTandas.Where(p => !existingTandaIds.Contains(p.Id)))
                _db.Deliveries.Add(new Delivery { TandaParticipantId = p.Id, Kind = DeliveryKind.Tanda, DeliveryRouteId = id, SortOrder = 999, Status = DeliveryStatus.Pending });

            await _db.SaveChangesAsync();

            var allCurrent = await _db.Deliveries
                .Include(d => d.Order).ThenInclude(o => o!.Client)
                .Include(d => d.TandaParticipant).ThenInclude(p => p!.Client)
                .Where(d => d.DeliveryRouteId == id)
                .ToListAsync();

            var locked = allCurrent
                .Where(d => d.Status == DeliveryStatus.Delivered || d.Status == DeliveryStatus.NotDelivered)
                .OrderBy(d => d.SortOrder).ToList();
            var pendingToOptimize = allCurrent
                .Where(d => d.Status != DeliveryStatus.Delivered && d.Status != DeliveryStatus.NotDelivered)
                .ToList();

            for (int i = 0; i < locked.Count; i++) locked[i].SortOrder = i + 1;
            int offsetForPending = locked.Count + 1;

            if (pendingToOptimize.Count > 0)
            {
                var centerLat = _config.GetValue<double>("Cami:RouteCenterLat", 27.4861);
                var centerLng = _config.GetValue<double>("Cami:RouteCenterLng", -99.5069);
                var stops = pendingToOptimize.Select(d =>
                {
                    var client = d.Kind == DeliveryKind.Tanda ? d.TandaParticipant?.Client : d.Order?.Client;
                    return new RouteStop(StopIdFor(d), client?.Latitude, client?.Longitude);
                }).ToList();
                var optimized = await _optimizer.OptimizeAsync(stops, centerLat, centerLng);
                var byStopId = pendingToOptimize.ToDictionary(StopIdFor, d => d);
                for (int i = 0; i < optimized.OrderedStopIds.Count; i++)
                    if (byStopId.TryGetValue(optimized.OrderedStopIds[i], out var d)) d.SortOrder = offsetForPending + i;
            }

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();

            try
            {
                await _hub.Clients.Group($"Route_{route.DriverToken}").SendAsync("RouteUpdated", new { id = route.Id });
                await _push.BroadcastToAllDriversAsync("🔄 Ruta actualizada", $"{route.Name} fue recompuesta.", new Dictionary<string, string> { { "action", "REFRESH_ROUTE" } });
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Error notificando recompose"); }

            var routeDto = await MapRouteDto(id);
            return Ok(new RecomposeRouteResponse(routeDto, skipped));
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "[Recompose] ERROR: {Message} | INNER: {Inner}", ex.Message, ex.InnerException?.Message ?? "");
            return StatusCode(500, new { message = "Error al recomponer la ruta.", detail = ex.Message });
        }
    }

    // ═══════════════════════════════════════════
    //  HELPERS & DELETE
    // ═══════════════════════════════════════════

    private async Task<RouteDto> MapRouteDto(int routeId)
    {
        var route = await _db.DeliveryRoutes.FirstAsync(r => r.Id == routeId);

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
            .Select(e => new DriverExpenseDto(e.Id, e.DeliveryRouteId, null, e.Amount, e.ExpenseType, e.Date, e.Notes, e.EvidencePath, e.CreatedAt))
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

            var linkedOrders = await _db.Orders.Where(o => o.DeliveryRouteId == id).ToListAsync();
            foreach (var order in linkedOrders)
            {
                order.DeliveryRouteId = null;
                if (order.Status == OrderStatus.InRoute) order.Status = OrderStatus.Pending;
            }

            var deliveryIds = route.Deliveries.Select(d => d.Id).ToList();

            var chats = await _db.ChatMessages
                .Where(c => c.DeliveryRouteId == id || (c.DeliveryId != null && deliveryIds.Contains(c.DeliveryId.Value)))
                .ToListAsync();
            if (chats.Any()) _db.ChatMessages.RemoveRange(chats);

            var expenses = await _db.DriverExpenses.Where(e => e.DeliveryRouteId == id).ToListAsync();
            if (expenses.Any()) _db.DriverExpenses.RemoveRange(expenses);

            var evidences = await _db.DeliveryEvidences.Where(e => deliveryIds.Contains(e.DeliveryId)).ToListAsync();
            if (evidences.Any()) _db.DeliveryEvidences.RemoveRange(evidences);

            if (route.Deliveries.Any()) _db.Deliveries.RemoveRange(route.Deliveries);
            _db.DeliveryRoutes.Remove(route);

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();

            try
            {
                await _hub.Clients.Group($"Route_{driverToken}").SendAsync("RouteDeleted", new { Message = $"La ruta '{routeName}' fue eliminada por el administrador." });
                await _push.BroadcastToAllDriversAsync("🚫 Ruta cancelada", $"La ruta {routeName} fue eliminada.");
            }
            catch { }

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
            var delivery = await _db.Deliveries.FirstOrDefaultAsync(d =>
                d.OrderId == order.Id && d.DeliveryRouteId == id);
            if (delivery != null && delivery.Status == DeliveryStatus.Pending)
            {
                delivery.Status = DeliveryStatus.Delivered;
                delivery.DeliveredAt = DateTime.UtcNow;
            }
        }

        var tandaDeliveries = await _db.Deliveries
            .Include(d => d.TandaParticipant)
            .Where(d => d.DeliveryRouteId == id && d.Kind == DeliveryKind.Tanda && d.Status == DeliveryStatus.Pending)
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
