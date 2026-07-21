using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Hubs; // <--- Importante
using EntregasApi.Models;
using EntregasApi.Services;
using System.Collections.Concurrent;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/driver/{driverToken}")]
public class DriverController : ControllerBase
{
    // ── Idempotency cache — evita cargos dobles por reintentos de red ──
    private static readonly ConcurrentDictionary<string, DateTime> _idempotencyCache = new();

    private readonly AppDbContext _db;
    private readonly IHubContext<DeliveryHub> _hub;
    private readonly IPushNotificationService _push;
    private readonly ICamiService _cami;
    private readonly ICloudinaryService _cloudinary;

    public DriverController(AppDbContext db, IHubContext<DeliveryHub> hub,
        IPushNotificationService push, ICamiService cami, ICloudinaryService cloudinary)
    {
        _db = db;
        _hub = hub;
        _push = push;
        _cami = cami;
        _cloudinary = cloudinary;
    }

    /// <summary>GET /api/driver/{token} - Obtener ruta del repartidor</summary>
    [HttpGet]
    public async Task<IActionResult> GetRoute(string driverToken)
    {
        var route = await _db.DeliveryRoutes
            .FirstOrDefaultAsync(r => r.DriverToken == driverToken);

        if (route == null) return NotFound("Ruta no encontrada.");

        var deliveries = await _db.Deliveries
            .Include(d => d.Order).ThenInclude(o => o!.Client)
            .Include(d => d.Order).ThenInclude(o => o!.Payments)
            .Include(d => d.Order).ThenInclude(o => o!.Items)
            .Include(d => d.Order).ThenInclude(o => o!.Packages)
            .Include(d => d.TandaParticipant).ThenInclude(p => p!.Client)
            .Include(d => d.TandaParticipant).ThenInclude(p => p!.Tanda).ThenInclude(t => t!.Product)
            .Include(d => d.Evidences)
            .Where(d => d.DeliveryRouteId == route.Id)
            .OrderBy(d => d.SortOrder)
            .ToListAsync();

        return Ok(new
        {
            route.Id,
            route.Name,
            Status = route.Status.ToString(),
            route.StartedAt,
            Deliveries = deliveries.Select(MapDeliveryToDto).ToList()
        });
    }

    private RouteDeliveryDto MapDeliveryToDto(Delivery d)
    {
        if (d.Kind == DeliveryKind.Tanda && d.TandaParticipant != null)
        {
            var participant = d.TandaParticipant;
            var tanda = participant.Tanda;
            var client = participant.Client;

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
                Packages: null,
                AlternativeAddress: null,
                ClientTag: client?.Tag.ToString() == "None" ? null : client?.Tag.ToString(),
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
            Packages: (order.Packages ?? new List<OrderPackage>()).Any()
                ? (order.Packages ?? new List<OrderPackage>())
                    .Select(p => new OrderPackageDto(p.Id, p.PackageNumber, p.QrCodeValue, p.Status.ToString(), p.CreatedAt, p.LoadedAt, p.DeliveredAt, p.ReturnedAt)).ToList()
                : null,
            AlternativeAddress: order.AlternativeAddress,
            ClientTag: order.Client.Tag.ToString() == "None" ? null : order.Client.Tag.ToString(),
            ClientType: order.Client.Type,
            Kind: "Order"
        );
    }


    // ═══════════════════════════════════════════
    //  OPERACIONES DE RUTA
    // ═══════════════════════════════════════════

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

        var firstDelivery = await _db.Deliveries
            .Include(d => d.Order)
            .Where(d => d.DeliveryRouteId == route.Id)
            .OrderBy(d => d.SortOrder)
            .FirstOrDefaultAsync();

        if (firstDelivery != null)
        {
            firstDelivery.Status = DeliveryStatus.InTransit;
            // Notificar clienta — solo aplica si es una Order regular (las tandas no tienen access token público).
            if (firstDelivery.Order != null)
            {
                await _hub.Clients.Group($"Order_{firstDelivery.Order.AccessToken}")
                    .SendAsync("DeliveryUpdate", new { Status = "InTransit", Message = "¡El repartidor va en camino hacia ti!" });

                if (firstDelivery.Order.ClientId > 0)
                    await _push.NotifyClientDriverEnRouteAsync(firstDelivery.Order.ClientId);
            }
        }

        await _db.SaveChangesAsync();

        // Notificar admin
        await _hub.Clients.Group("Admins").SendAsync("RouteStarted", new { RouteId = route.Id });

        return Ok(new { message = "Ruta iniciada.", firstDeliveryId = firstDelivery?.Id });
    }

    [HttpPost("transit/{deliveryId}")]
    public async Task<IActionResult> MarkInTransit(string driverToken, int deliveryId)
    {
        var route = await _db.DeliveryRoutes.FirstOrDefaultAsync(r => r.DriverToken == driverToken);
        if (route == null) return NotFound("Ruta no encontrada.");

        var delivery = await _db.Deliveries
            .Include(d => d.Order)
            .FirstOrDefaultAsync(d => d.Id == deliveryId && d.DeliveryRouteId == route.Id);
        if (delivery == null) return NotFound("Entrega no encontrada.");

        // Reset anteriores InTransit
        var previousInTransit = await _db.Deliveries
            .Where(d => d.DeliveryRouteId == route.Id && d.Status == DeliveryStatus.InTransit)
            .ToListAsync();

        foreach (var prev in previousInTransit) prev.Status = DeliveryStatus.Pending;

        delivery.Status = DeliveryStatus.InTransit;
        if (delivery.ArrivedAt == null)
            delivery.ArrivedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Admin siempre se entera
        await _hub.Clients.Group($"Route_{driverToken}")
             .SendAsync("DeliveryStatusUpdate", new { delivery.Id, Status = "InTransit" });

        // Notificaciones a clienta y greeting de CAMI sólo aplican a Orders regulares.
        if (delivery.Order != null)
        {
            await _hub.Clients.Group($"Order_{delivery.Order.AccessToken}")
                .SendAsync("DeliveryUpdate", new { Status = "InTransit", Message = "¡El repartidor va en camino hacia ti!" });

            if (delivery.Order.ClientId > 0)
                await _push.NotifyClientDriverEnRouteAsync(delivery.Order.ClientId);

            var orderForCami = await _db.Orders
                .Include(o => o.Items)
                .Include(o => o.Client)
                .FirstOrDefaultAsync(o => o.Id == delivery.OrderId);
            var camiAccessToken = delivery.Order.AccessToken;

            _ = Task.Run(async () =>
            {
                try
                {
                    if (orderForCami != null)
                    {
                        var greeting = await _cami.GetProactiveGreetingAsync(orderForCami);
                        await _hub.Clients.Group($"Order_{camiAccessToken}")
                            .SendAsync("CamiGreeting", new { greeting.Message, greeting.AudioBase64 });
                    }
                }
                catch { /* No bloquea el flujo principal */ }
            });
        }

        return Ok(new { message = "Entrega marcada en tránsito." });
    }

    [HttpPut("reorder")]
    public async Task<IActionResult> ReorderDeliveries(string driverToken, [FromBody] List<int> orderedDeliveryIds)
    {
        var route = await _db.DeliveryRoutes.FirstOrDefaultAsync(r => r.DriverToken == driverToken);
        if (route == null) return NotFound("Ruta no encontrada.");

        if (route.Status == RouteStatus.Completed)
            return BadRequest("No se puede reordenar una ruta completada.");

        var deliveries = await _db.Deliveries
            .Where(d => d.DeliveryRouteId == route.Id)
            .ToListAsync();

        if (deliveries.Count != orderedDeliveryIds.Count || !deliveries.All(d => orderedDeliveryIds.Contains(d.Id)))
        {
            return BadRequest("La lista de IDs proporcionada no coincide con los pedidos de la ruta.");
        }

        // Aplicamos el nuevo orden a todos
        for (int i = 0; i < orderedDeliveryIds.Count; i++)
        {
            var delivery = deliveries.First(d => d.Id == orderedDeliveryIds[i]);
            delivery.SortOrder = i + 1;
        }

        // Identificamos al primero de la lista (que esté pendiente) para forzarlo a InTransit si la ruta ya está activa
        if (route.Status == RouteStatus.Active)
        {
            var firstPending = deliveries.Where(d => d.Status == DeliveryStatus.Pending || d.Status == DeliveryStatus.InTransit).OrderBy(d => d.SortOrder).FirstOrDefault();
            if (firstPending != null && firstPending.Status == DeliveryStatus.Pending)
            {
                // Significa que alguien arrastró a alguien nuevo a la posición 1.
                // Reset a todos los demás En Tránsito a Pendiente
                foreach (var d in deliveries.Where(d => d.Status == DeliveryStatus.InTransit)) d.Status = DeliveryStatus.Pending;
                firstPending.Status = DeliveryStatus.InTransit;
                
                await _db.SaveChangesAsync(); // Guardar el cambio a InTransit
                
                // Disparar las alertas para ese nuevo usuario
                if (firstPending.Order != null) 
                {
                    await _hub.Clients.Group($"Order_{firstPending.Order.AccessToken}")
                        .SendAsync("DeliveryUpdate", new { Status = "InTransit", Message = "¡El repartidor va en camino hacia ti!" });
                    
                    if (firstPending.Order.ClientId > 0)
                        await _push.NotifyClientDriverEnRouteAsync(firstPending.Order.ClientId);
                }
            }
        }

        await _db.SaveChangesAsync();

        // Notificamos al Dashboard Admin que el orden en vivo cambió desde el móvil del chofer
        await _hub.Clients.Group("Admins").SendAsync("RouteUpdated", route.Id);

        return Ok(new { message = "Ruta reordenada exitosamente por el repartidor." });
    }

    /// <summary>POST /api/driver/{token}/packages/scan — Escanear bolsa al cargar o entregar</summary>
    [HttpPost("packages/scan")]
    public async Task<IActionResult> ScanPackage(string driverToken, [FromBody] ScanPackageRequest req)
    {
        // 1. Validar token de ruta
        var route = await _db.DeliveryRoutes
            .Include(r => r.Deliveries)
            .FirstOrDefaultAsync(r => r.DriverToken == driverToken);

        if (route == null) return NotFound(new { message = "Ruta no encontrada." });

        // 2. Buscar paquete por QR
        var package = await _db.OrderPackages
            .Include(p => p.Order)
                .ThenInclude(o => o.Packages)
            .FirstOrDefaultAsync(p => p.QrCodeValue == req.QrCodeValue);

        if (package == null)
            return NotFound(new { message = "Código QR no reconocido. Esta bolsa no es del sistema." });

        // 3. Seguridad — el paquete debe pertenecer a un pedido de esta ruta
        var routeDelivery = route.Deliveries.FirstOrDefault(d => d.OrderId == package.OrderId);
        if (routeDelivery == null)
            return BadRequest(new { message = "Esta bolsa no pertenece a tu ruta de hoy." });
        if (routeDelivery.Status is DeliveryStatus.Delivered or DeliveryStatus.NotDelivered)
            return BadRequest(new { message = "Este pedido ya no esta pendiente en esta ruta." });

        string successMessage;

        // 4. Máquina de estados
        if (req.Action.Equals("Load", StringComparison.OrdinalIgnoreCase))
        {
            if (package.Status == PackageTrackingStatus.Loaded)
                return BadRequest(new { message = $"La bolsa #{package.PackageNumber} ya estaba cargada." });

            if (package.Status == PackageTrackingStatus.Delivered)
                return BadRequest(new { message = $"La bolsa #{package.PackageNumber} ya fue entregada y no puede cargarse de nuevo." });

            package.Status = PackageTrackingStatus.Loaded;
            package.LoadedAt = DateTime.UtcNow;
            successMessage = $"Bolsa #{package.PackageNumber} cargada ✓";
        }
        else if (req.Action.Equals("Deliver", StringComparison.OrdinalIgnoreCase))
        {
            if (package.Status != PackageTrackingStatus.Loaded && package.Status != PackageTrackingStatus.Delivered)
                return BadRequest(new { message = $"¡Alerta! La bolsa #{package.PackageNumber} no fue cargada al vehículo." });

            if (package.Status == PackageTrackingStatus.Delivered)
                return BadRequest(new { message = $"La bolsa #{package.PackageNumber} ya fue entregada anteriormente." });

            package.Status = PackageTrackingStatus.Delivered;
            package.DeliveredAt = DateTime.UtcNow;
            successMessage = $"Bolsa #{package.PackageNumber} entregada ✓";
        }
        else if (req.Action.Equals("Return", StringComparison.OrdinalIgnoreCase))
        {
            if (package.Status == PackageTrackingStatus.Delivered)
                return BadRequest(new { message = $"La bolsa #{package.PackageNumber} ya fue entregada, no se puede devolver." });

            if (package.Status == PackageTrackingStatus.Returned)
                return BadRequest(new { message = $"La bolsa #{package.PackageNumber} ya fue marcada como devuelta." });

            package.Status = PackageTrackingStatus.Returned;
            package.ReturnedAt = DateTime.UtcNow;
            successMessage = $"Bolsa #{package.PackageNumber} devuelta ↩️";
        }
        else
        {
            return BadRequest(new { message = "Acción inválida. Usa 'Load', 'Deliver' o 'Return'." });
        }

        // 5. Actualizar banderas de la orden
        var order = package.Order;
        order.IsFullyLoaded = order.Packages.All(p =>
            p.Status is PackageTrackingStatus.Loaded or PackageTrackingStatus.Delivered);

        await _db.SaveChangesAsync();

        return Ok(new
        {
            message = successMessage,
            packageNumber = package.PackageNumber,
            allLoaded = order.IsFullyLoaded
        });
    }

    [HttpPost("location")]
    public async Task<IActionResult> UpdateLocation(string driverToken, UpdateLocationRequest req)
    {
        var route = await _db.DeliveryRoutes.FirstOrDefaultAsync(r => r.DriverToken == driverToken);
        if (route == null) return NotFound();

        route.CurrentLatitude = req.Latitude;
        route.CurrentLongitude = req.Longitude;
        route.LastLocationUpdate = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Notificar al Admin y a las Clientas de esta ruta en tiempo real (solo a los de esta ruta)
        await _hub.Clients.Group("Admins")
             .SendAsync("ReceiveLocation", driverToken, req.Latitude, req.Longitude);
        
        await _hub.Clients.Group($"Tracking_{driverToken}")
             .SendAsync("LocationUpdate", new { latitude = req.Latitude, longitude = req.Longitude });

        return Ok();
    }

    [HttpPost("delivery/{deliveryId}/coordinates")]
    public async Task<IActionResult> SetDeliveryCoordinates(string driverToken, int deliveryId, [FromBody] UpdateLocationRequest req)
    {
        var route = await _db.DeliveryRoutes.FirstOrDefaultAsync(r => r.DriverToken == driverToken);
        if (route == null) return NotFound("Ruta no encontrada.");

        var delivery = await _db.Deliveries
            .Include(d => d.Order)
                .ThenInclude(o => o!.Client)
            .Include(d => d.TandaParticipant)
                .ThenInclude(p => p!.Client)
            .FirstOrDefaultAsync(d => d.Id == deliveryId && d.DeliveryRouteId == route.Id);

        if (delivery == null) return NotFound("Entrega no encontrada.");

        var client = delivery.Kind == DeliveryKind.Tanda
            ? delivery.TandaParticipant?.Client
            : delivery.Order?.Client;

        if (client == null) return NotFound("Cliente no encontrado.");

        client.Latitude = req.Latitude;
        client.Longitude = req.Longitude;

        await _db.SaveChangesAsync();

        return Ok(new { message = "Coordenadas actualizadas correctamente." });
    }

    [HttpPost("deliver/{deliveryId}")]
    public async Task<IActionResult> MarkDelivered(string driverToken, int deliveryId,
        [FromForm] CompleteDeliveryRequest req, [FromForm] List<IFormFile>? photos)
    {
        // Idempotency — si el cliente reintenta por timeout, devolver 200 inmediatamente
        var iKey = Request.Headers["X-Idempotency-Key"].ToString();
        if (!string.IsNullOrEmpty(iKey) && !_idempotencyCache.TryAdd(iKey, DateTime.UtcNow))
            return Ok(new { message = "Entrega ya procesada (idempotente)." });

        var route = await _db.DeliveryRoutes.FirstOrDefaultAsync(r => r.DriverToken == driverToken);
        if (route == null) return NotFound("Ruta no encontrada.");

        // IMPORTANTE: Incluimos al Cliente para poder sumarle los puntos
        var delivery = await _db.Deliveries
            .Include(d => d.Order)
                .ThenInclude(o => o!.Client)
            .Include(d => d.Order)
                .ThenInclude(o => o!.Payments)
            .Include(d => d.TandaParticipant)
                .ThenInclude(p => p!.Client)
            .FirstOrDefaultAsync(d => d.Id == deliveryId && d.DeliveryRouteId == route.Id);

        if (delivery == null) return NotFound("Entrega no encontrada.");

        // ── Flujo tanda: sin pagos, sin puntos, sin Order ──
        if (delivery.Kind == DeliveryKind.Tanda)
        {
            if (delivery.Status != DeliveryStatus.Delivered)
            {
                delivery.Status = DeliveryStatus.Delivered;
                delivery.DeliveredAt = DateTime.UtcNow;
                delivery.Notes = req.Notes;
                if (!string.IsNullOrWhiteSpace(req.SignatureSvg))
                {
                    delivery.SignatureSvg = req.SignatureSvg;
                    delivery.SignedAt = DateTime.UtcNow;
                    delivery.SignedByName = string.IsNullOrWhiteSpace(req.SignedByName) ? null : req.SignedByName;
                }
                if (delivery.TandaParticipant != null)
                {
                    delivery.TandaParticipant.IsDelivered = true;
                    delivery.TandaParticipant.DeliveryDate = DateTime.UtcNow;
                }
            }

            if (photos != null) await SavePhotos(delivery, photos, EvidenceType.DeliveryProof);
            await _db.SaveChangesAsync();

            await _hub.Clients.Group($"Route_{driverToken}")
                .SendAsync("DeliveryStatusUpdate", new { delivery.Id, Status = "Delivered" });
            await _hub.Clients.Group("Admins").SendAsync("DeliveryUpdate", new
            {
                DeliveryId = delivery.Id,
                Status = "Delivered",
                Kind = "Tanda",
                TandaParticipantId = delivery.TandaParticipantId,
                ClientName = delivery.TandaParticipant?.Client?.Name
            });

            var nextIdT = await AutoAdvanceToNext(route.Id, delivery.SortOrder);
            await CheckRouteCompletion(route.Id);

            return Ok(new { message = "Entrega de tanda registrada.", nextDeliveryId = nextIdT });
        }

        // ── Flujo orden regular ──
        // Solo procesamos si no estaba ya entregado (para evitar doble suma de puntos si le pican dos veces)
        List<PaymentInputDto>? parsedPayments = null;
        if (delivery.Status != DeliveryStatus.Delivered)
        {
            delivery.Status = DeliveryStatus.Delivered;
            delivery.DeliveredAt = DateTime.UtcNow;
            delivery.Notes = req.Notes;
            // Firma digital capturada en la app del conductor (opcional, no todas las clientas firman)
            if (!string.IsNullOrWhiteSpace(req.SignatureSvg))
            {
                delivery.SignatureSvg = req.SignatureSvg;
                delivery.SignedAt = DateTime.UtcNow;
                delivery.SignedByName = string.IsNullOrWhiteSpace(req.SignedByName) ? null : req.SignedByName;
            }
            delivery.Order!.Status = Models.OrderStatus.Delivered;
            if (!string.IsNullOrWhiteSpace(req.PaymentsJson))
            {
                try
                {
                    parsedPayments = System.Text.Json.JsonSerializer.Deserialize<List<PaymentInputDto>>(
                        req.PaymentsJson, 
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                    );
                }
                catch { /* ignore json parse errors in production for safety, rely on null check */}
            }

            // Registrar pagos del chofer
            if (parsedPayments != null && parsedPayments.Any())
            {
                foreach (var p in parsedPayments)
                {
                    _db.OrderPayments.Add(new OrderPayment
                    {
                        OrderId = delivery.OrderId!.Value,
                        Amount = p.Amount,
                        Method = p.Method,
                        Date = DateTime.UtcNow,
                        RegisteredBy = "Driver",
                        Notes = p.Notes
                    });
                }

                var amountCollected = parsedPayments.Sum(x => x.Amount);
                if (amountCollected > 0)
                {
                    await _push.SendNotificationToAdminsAsync(
                        "💰 Pago Registrado por Repartidor",
                        $"Se ingresaron {amountCollected:C} del pedido #{delivery.Order!.Id} ({delivery.Order.Client?.Name})",
                        tag: "payment-received"
                    );
                }
            }

            // -----------------------------------------------------------
            // 🎀 LÓGICA DE REGIPUNTOS (10 pts por cada $100)
            // -----------------------------------------------------------
            int puntosGanados = (int)(delivery.Order!.Total / 10m);
            if (puntosGanados > 0 && delivery.Order.Client != null)
            {
                var transaccion = new LoyaltyTransaction
                {
                    ClientId = delivery.Order.Client.Id,
                    Points = puntosGanados,
                    Reason = $"Entrega exitosa de ruta #{delivery.Order.Id}",
                    Date = DateTime.UtcNow
                };
                _db.LoyaltyTransactions.Add(transaccion);

                delivery.Order.Client.CurrentPoints += puntosGanados;
                delivery.Order.Client.LifetimePoints += puntosGanados;
                delivery.Order.Client.Type = "Frecuente"; // Sube de categoría
            }
        }

        if (photos != null) await SavePhotos(delivery, photos, EvidenceType.DeliveryProof);

        await _db.SaveChangesAsync();

        if (delivery.Order!.ClientId > 0)
            await _push.NotifyClientDeliveredAsync(delivery.Order.ClientId);

        // Notificar en tiempo real
        await _hub.Clients.Group($"Order_{delivery.Order.AccessToken}").SendAsync("DeliveryUpdate", new { Status = "Delivered" });
        await _hub.Clients.Group($"Route_{driverToken}").SendAsync("DeliveryStatusUpdate", new { delivery.Id, Status = "Delivered" });

        // ✨ PROPAGACIÓN MAGISTRAL ADEMÁS AL GRUPO DE ADMINS
        await _hub.Clients.Group("Admins").SendAsync("DeliveryUpdate", new {
            OrderId = delivery.Order.Id,
            Status = "Delivered",
            ClientName = delivery.Order.Client?.Name,
            AmountReceived = parsedPayments?.Sum(p => p.Amount) ?? 0
        });

        var nextId = await AutoAdvanceToNext(route.Id, delivery.SortOrder);
        await CheckRouteCompletion(route.Id);

        return Ok(new { message = "Entrega registrada y puntos asignados.", nextDeliveryId = nextId });
    }

    [HttpPost("fail/{deliveryId}")]
    public async Task<IActionResult> MarkFailed(string driverToken, int deliveryId,
        [FromForm] FailDeliveryRequest req, [FromForm] List<IFormFile>? photos)
    {
        // Idempotency
        var iKey = Request.Headers["X-Idempotency-Key"].ToString();
        if (!string.IsNullOrEmpty(iKey) && !_idempotencyCache.TryAdd(iKey, DateTime.UtcNow))
            return Ok(new { message = "No-entrega ya procesada (idempotente)." });

        var route = await _db.DeliveryRoutes.FirstOrDefaultAsync(r => r.DriverToken == driverToken);
        if (route == null) return NotFound();

        var delivery = await _db.Deliveries
            .Include(d => d.Order).ThenInclude(o => o!.Client)
            .Include(d => d.TandaParticipant).ThenInclude(p => p!.Client)
            .FirstOrDefaultAsync(d => d.Id == deliveryId && d.DeliveryRouteId == route.Id);
        if (delivery == null) return NotFound();

        delivery.Status = DeliveryStatus.NotDelivered;
        delivery.FailureReason = req.Reason;
        delivery.Notes = req.Notes;
        delivery.DeliveredAt = DateTime.UtcNow;
        // En orden regular: marcamos también la Order. En tanda no tocamos IsDelivered (sigue false).
        if (delivery.Order != null)
            DeliveryRetryPolicy.ReleaseForRetry(delivery.Order);

        if (photos != null) await SavePhotos(delivery, photos, EvidenceType.NonDeliveryProof);

        await _db.SaveChangesAsync();

        var clientName = delivery.Order?.Client?.Name
            ?? delivery.TandaParticipant?.Client?.Name
            ?? "Cliente";
        await _push.SendNotificationToAdminsAsync(
            "⚠️ Entrega Fallida",
            $"{clientName} no recibió el pedido: {req.Reason}",
            tag: "delivery-failed"
        );

        // Notificar Admin Dashboard
        await _hub.Clients.Group($"Route_{driverToken}").SendAsync("DeliveryStatusUpdate", new { delivery.Id, Status = "NotDelivered" });
        await _hub.Clients.Group("Admins").SendAsync("DeliveryUpdate", new {
            DeliveryId = delivery.Id,
            OrderId = delivery.OrderId,
            TandaParticipantId = delivery.TandaParticipantId,
            Kind = delivery.Kind.ToString(),
            Status = delivery.Order?.Status.ToString() ?? "NotDelivered",
            DeliveryStatus = "NotDelivered",
            Reason = req.Reason
        });

        var nextId = await AutoAdvanceToNext(route.Id, delivery.SortOrder);
        await CheckRouteCompletion(route.Id);

        return Ok(new { message = "No-entrega registrada.", nextDeliveryId = nextId });
    }

    // --- Helpers Privados ---
    private async Task<int?> AutoAdvanceToNext(int routeId, int currentSortOrder)
    {
        var nextDelivery = await _db.Deliveries
            .Include(d => d.Order)
            .Where(d => d.DeliveryRouteId == routeId && d.Status == DeliveryStatus.Pending && d.SortOrder > currentSortOrder)
            .OrderBy(d => d.SortOrder)
            .FirstOrDefaultAsync();

        if (nextDelivery != null)
        {
            nextDelivery.Status = DeliveryStatus.InTransit;
            await _db.SaveChangesAsync();

            // Notificación a la clienta sólo si la siguiente parada es una Order regular.
            if (nextDelivery.Order != null)
            {
                await _hub.Clients.Group($"Order_{nextDelivery.Order.AccessToken}")
                    .SendAsync("DeliveryUpdate", new { Status = "InTransit", Message = "¡Tu turno! El repartidor va hacia ti." });

                if (nextDelivery.Order.ClientId > 0)
                    await _push.NotifyClientDriverEnRouteAsync(nextDelivery.Order.ClientId);
            }

            return nextDelivery.Id;
        }
        return null;
    }

    private async Task SavePhotos(Delivery delivery, List<IFormFile> photos, EvidenceType type)
    {
        foreach (var photo in photos.Where(p => p.Length > 0))
        {
            using var stream = photo.OpenReadStream();
            var url = await _cloudinary.UploadAsync(stream, photo.FileName, "evidence");
            _db.DeliveryEvidences.Add(new DeliveryEvidence
            {
                DeliveryId = delivery.Id,
                ImagePath = url,   // URL permanente de Cloudinary
                Type = type
            });
        }
    }

    private async Task CheckRouteCompletion(int routeId)
    {
        var allDone = !await _db.Deliveries.AnyAsync(d => d.DeliveryRouteId == routeId && (d.Status == DeliveryStatus.Pending || d.Status == DeliveryStatus.InTransit));
        if (!allDone) return;

        var route = await _db.DeliveryRoutes.FindAsync(routeId);
        if (route == null) return;

        bool alreadyCompleted = route.Status == RouteStatus.Completed;

        route.Status = RouteStatus.Completed;
        route.CompletedAt = DateTime.UtcNow;

        // ── Auto-conciliación de bolsas sin resolver ──
        // Carga todos los paquetes que aún estén en estado Loaded (ni entregados ni devueltos)
        var loadedPackages = await _db.OrderPackages
            .Include(p => p.Order)
            .Where(p => p.Status == PackageTrackingStatus.Loaded
                && _db.Deliveries.Any(d => d.OrderId == p.OrderId && d.DeliveryRouteId == routeId))
            .ToListAsync();

        var deliveryByOrderId = await _db.Deliveries
            .Where(d => d.DeliveryRouteId == routeId && d.OrderId != null)
            .ToDictionaryAsync(d => d.OrderId!.Value);

        int autoReturned = 0;
        foreach (var pkg in loadedPackages)
        {
            if (!deliveryByOrderId.TryGetValue(pkg.OrderId, out var delivery)) continue;

            var deliveryStatus = delivery.Status;
            if (deliveryStatus == DeliveryStatus.Delivered)
            {
                pkg.Status = PackageTrackingStatus.Delivered;
                pkg.DeliveredAt = delivery.DeliveredAt ?? DateTime.UtcNow;
            }
            else
            {
                // NotDelivered o cualquier otro estado terminal → devuelta
                pkg.Status = PackageTrackingStatus.Returned;
                pkg.ReturnedAt = DateTime.UtcNow;
                if (pkg.Order != null) pkg.Order.IsFullyLoaded = false;
                autoReturned++;
            }
        }

        await _db.SaveChangesAsync();

        if (autoReturned > 0)
        {
            await _push.SendNotificationToAdminsAsync(
                "↩️ Bolsas devueltas al terminar ruta",
                $"{autoReturned} bolsa(s) regresaron sin entregar en la ruta #{routeId}. Revisa el inventario.",
                tag: "packages-returned"
            );
        }

        // 🏁 Resumen de ruta terminada (solo la primera vez que se completa).
        if (!alreadyCompleted)
        {
            var routeDeliveries = await _db.Deliveries
                .Include(d => d.Order)
                .Where(d => d.DeliveryRouteId == routeId)
                .ToListAsync();

            int entregadas = routeDeliveries.Count(d => d.Status == DeliveryStatus.Delivered);
            int noEntregadas = routeDeliveries.Count(d => d.Status == DeliveryStatus.NotDelivered);
            decimal totalEntregado = routeDeliveries
                .Where(d => d.Status == DeliveryStatus.Delivered && d.Order != null)
                .Sum(d => d.Order!.Total);

            var resumen = noEntregadas > 0
                ? $"{entregadas} entregadas ✅, {noEntregadas} sin entregar 📦. Total entregado: ${totalEntregado:N0} 💰"
                : $"¡Todas entregadas! {entregadas} entregas ✅. Total: ${totalEntregado:N0} 🎉";

            await _push.SendNotificationToAdminsAsync(
                "🏁 Ruta terminada",
                resumen,
                url: "/admin/routes",
                tag: "route-completed"
            );
        }
    }

    [HttpPost("expenses")]
    public async Task<IActionResult> AddExpense(string driverToken, [FromForm] decimal amount, [FromForm] string expenseType, [FromForm] string? notes, IFormFile? photo)
    {
        var route = await _db.DeliveryRoutes.FirstOrDefaultAsync(r => r.DriverToken == driverToken);
        if (route == null) return NotFound();

        var expense = new DriverExpense
        {
            DeliveryRouteId = route.Id,
            Amount = amount,
            ExpenseType = expenseType,
            Notes = notes?.Trim(),
            Date = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };

        if (photo != null && photo.Length > 0)
        {
            using var stream = photo.OpenReadStream();
            expense.EvidencePath = await _cloudinary.UploadAsync(stream, photo.FileName, "expenses");
        }

        _db.DriverExpenses.Add(expense);
        await _db.SaveChangesAsync();

        // ✨ Notificar admin del gasto en tiempo real
        await _hub.Clients.Group("Admins").SendAsync("ExpenseAdded", new { 
            RouteId = route.Id, 
            Amount = expense.Amount, 
            Type = expense.ExpenseType 
        });

        return Ok(expense);
    }


    // ═══════════════════════════════════════════
    //  CHAT CHOFER ↔️ ADMIN
    // ═══════════════════════════════════════════

    [HttpGet("chat")]
    public async Task<IActionResult> GetChat(string driverToken)
    {
        var route = await _db.DeliveryRoutes.FirstOrDefaultAsync(r => r.DriverToken == driverToken);
        if (route == null) return NotFound();

        var msgs = await _db.ChatMessages
            .Where(m => m.DeliveryRouteId == route.Id && m.DeliveryId == null) // Filtro para que solo vea los del admin
            .OrderBy(m => m.Timestamp)
            .Select(m => new {
                id = m.Id,
                sender = m.Sender,
                text = m.Text,
                timestamp = m.Timestamp
            })
            .ToListAsync();

        return Ok(msgs);
    }

    [HttpPost("chat")]
    public async Task<IActionResult> SendDriverMessage(string driverToken, [FromBody] SendMessageRequest req)
    {
        var route = await _db.DeliveryRoutes.FirstOrDefaultAsync(r => r.DriverToken == driverToken);
        if (route == null) return NotFound();

        var msg = new ChatMessage
        {
            DeliveryRouteId = route.Id,
            Sender = "Driver",
            Text = req.Text,
            Timestamp = DateTime.UtcNow
        };

        _db.ChatMessages.Add(msg);
        await _db.SaveChangesAsync();

        var msgDto = new { id = msg.Id, sender = msg.Sender, text = msg.Text, timestamp = msg.Timestamp, deliveryRouteId = msg.DeliveryRouteId };

        await _hub.Clients.Group($"Route_{driverToken}").SendAsync("ReceiveChatMessage", msgDto);
        await _hub.Clients.Group("Admins").SendAsync("ReceiveChatMessage", msgDto);

        return Ok(msgDto);
    }

    // ═══════════════════════════════════════════
    //  CHAT CHOFER ↔️ CLIENTA
    // ═══════════════════════════════════════════

    [HttpGet("deliver/{deliveryId}/chat")]
    public async Task<IActionResult> GetClientChat(string driverToken, int deliveryId)
    {
        var route = await _db.DeliveryRoutes.FirstOrDefaultAsync(r => r.DriverToken == driverToken);
        if (route == null) return NotFound();

        var msgs = await _db.ChatMessages
            .Where(m => m.DeliveryRouteId == route.Id && m.DeliveryId == deliveryId)
            .OrderBy(m => m.Timestamp)
            .Select(m => new {
                id = m.Id,
                sender = m.Sender,
                text = m.Text,
                timestamp = m.Timestamp
            })
            .ToListAsync();

        return Ok(msgs);
    }

    [HttpPost("deliver/{deliveryId}/chat")]
    public async Task<IActionResult> SendMessageToClient(string driverToken, int deliveryId, [FromBody] SendMessageRequest req)
    {
        var route = await _db.DeliveryRoutes.FirstOrDefaultAsync(r => r.DriverToken == driverToken);
        if (route == null) return NotFound("Ruta no encontrada.");

        var delivery = await _db.Deliveries.Include(d => d.Order)
            .FirstOrDefaultAsync(d => d.Id == deliveryId && d.DeliveryRouteId == route.Id);

        if (delivery == null) return NotFound("Entrega no encontrada.");

        // Las entregas de tanda no tienen canal público de chat con la clienta.
        if (delivery.Order == null)
            return BadRequest("Esta entrega es una tanda y no admite chat con la clienta.");

        var msg = new ChatMessage
        {
            DeliveryRouteId = route.Id,
            DeliveryId = delivery.Id,
            Sender = "Driver",
            Text = req.Text,
            Timestamp = DateTime.UtcNow
        };

        _db.ChatMessages.Add(msg);
        await _db.SaveChangesAsync();

        var msgDto = new { id = msg.Id, deliveryId = msg.DeliveryId, sender = msg.Sender, text = msg.Text, timestamp = msg.Timestamp };

        // 🔔 ¡Ring ring! Le avisamos a la clienta por SignalR
        await _hub.Clients.Group($"Order_{delivery.Order.AccessToken}")
            .SendAsync("ReceiveClientChatMessage", msgDto);

        // ✨ ADEMÁS PROPAGAMOS AL GRUPO DE ADMINS
        await _hub.Clients.Group("Admins")
            .SendAsync("ReceiveClientChatMessage", msgDto);

        return Ok(msgDto);
    }

    [HttpPost("cami-command")]
    public async Task<ActionResult<DriverCamiResponse>> CamiCommand(string driverToken, [FromBody] DriverCamiRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.CommandText))
            return BadRequest("El comando no puede estar vacío.");

        var responseText = await _cami.ProcessDriverCommandAsync(driverToken, req.CommandText);

        return Ok(new DriverCamiResponse(responseText));
    }
}
