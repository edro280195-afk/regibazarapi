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
            .Include(d => d.Order).ThenInclude(o => o.Client)
            .Include(d => d.Order).ThenInclude(o => o.Payments)
            .Include(d => d.Order).ThenInclude(o => o.Items)
            .Include(d => d.Order).ThenInclude(o => o.Packages)
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
                EvidenceUrls: d.Evidences.Select(e => e.ImagePath).ToList(),
                ClientPhone: d.Order.Client.Phone,
                PaymentMethod: d.Order.PaymentMethod,
                Payments: (d.Order.Payments ?? new List<OrderPayment>())
                    .Select(p => new OrderPaymentDto(p.Id, p.OrderId, p.Amount, p.Method, p.Date, p.RegisteredBy, p.Notes)).ToList(),
                Items: (d.Order.Items ?? new List<OrderItem>())
                    .Select(i => new OrderItemDto(i.Id, i.ProductName, i.Quantity, i.UnitPrice, i.LineTotal)).ToList(),
                AmountPaid: d.Order.AmountPaid,
                BalanceDue: d.Order.BalanceDue,
                DeliveryInstructions: d.Order.Client.DeliveryInstructions,
                ArrivedAt: d.ArrivedAt,
                Packages: (d.Order.Packages ?? new List<OrderPackage>()).Any()
                    ? (d.Order.Packages ?? new List<OrderPackage>())
                        .Select(p => new OrderPackageDto(p.Id, p.PackageNumber, p.QrCodeValue, p.Status.ToString(), p.CreatedAt, p.LoadedAt, p.DeliveredAt, p.ReturnedAt)).ToList()
                    : null,
                AlternativeAddress: d.Order.AlternativeAddress,
                // Feature #5 — Etiqueta y tipo de cliente para el chofer
                ClientTag: d.Order.Client.Tag.ToString() == "None" ? null : d.Order.Client.Tag.ToString(),
                ClientType: d.Order.Client.Type
            )).ToList()
        });
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
            // Notificar clienta
            await _hub.Clients.Group($"Order_{firstDelivery.Order.AccessToken}")
                .SendAsync("DeliveryUpdate", new { Status = "InTransit", Message = "¡El repartidor va en camino hacia ti!" });
            
            if (firstDelivery.Order.ClientId > 0)
                await _push.NotifyClientDriverEnRouteAsync(firstDelivery.Order.ClientId);
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
        // Registrar momento de llegada (solo la primera vez)
        if (delivery.ArrivedAt == null)
            delivery.ArrivedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Notificaciones
        await _hub.Clients.Group($"Order_{delivery.Order.AccessToken}")
            .SendAsync("DeliveryUpdate", new { Status = "InTransit", Message = "¡El repartidor va en camino hacia ti!" });

        if (delivery.Order.ClientId > 0)
            await _push.NotifyClientDriverEnRouteAsync(delivery.Order.ClientId);

        // Admin
        await _hub.Clients.Group($"Route_{driverToken}")
             .SendAsync("DeliveryStatusUpdate", new { delivery.Id, Status = "InTransit" });

        // Feature #9 — Saludo proactivo de CAMI a la clienta (fire-and-forget)
        // Pre-cargar la orden aquí (dentro del scope del request) para evitar usar _db
        // después de que el DbContext haya sido eliminado al finalizar la solicitud HTTP.
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
        var routeOrderIds = route.Deliveries.Select(d => d.OrderId).ToHashSet();
        if (!routeOrderIds.Contains(package.OrderId))
            return BadRequest(new { message = "Esta bolsa no pertenece a tu ruta de hoy." });

        string successMessage;

        // 4. Máquina de estados
        if (req.Action.Equals("Load", StringComparison.OrdinalIgnoreCase))
        {
            if (package.Status >= PackageTrackingStatus.Loaded)
                return BadRequest(new { message = $"La bolsa #{package.PackageNumber} ya estaba cargada." });

            package.Status = PackageTrackingStatus.Loaded;
            package.LoadedAt = DateTime.UtcNow;
            successMessage = $"Bolsa #{package.PackageNumber} cargada ✓";
        }
        else if (req.Action.Equals("Deliver", StringComparison.OrdinalIgnoreCase))
        {
            if (package.Status == PackageTrackingStatus.Packed)
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
        order.IsFullyLoaded = order.Packages.All(p => p.Status >= PackageTrackingStatus.Loaded);

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
                .ThenInclude(o => o.Client)
            .FirstOrDefaultAsync(d => d.Id == deliveryId && d.DeliveryRouteId == route.Id);

        if (delivery == null || delivery.Order.Client == null)
            return NotFound("Entrega o cliente no encontrado.");

        delivery.Order.Client.Latitude = req.Latitude;
        delivery.Order.Client.Longitude = req.Longitude;

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
                .ThenInclude(o => o.Client)
            .Include(d => d.Order)
                .ThenInclude(o => o.Payments)
            .FirstOrDefaultAsync(d => d.Id == deliveryId && d.DeliveryRouteId == route.Id);

        if (delivery == null) return NotFound("Entrega no encontrada.");

        // Solo procesamos si no estaba ya entregado (para evitar doble suma de puntos si le pican dos veces)
        List<PaymentInputDto>? parsedPayments = null;
        if (delivery.Status != DeliveryStatus.Delivered)
        {
            delivery.Status = DeliveryStatus.Delivered;
            delivery.DeliveredAt = DateTime.UtcNow;
            delivery.Notes = req.Notes;
            delivery.Order.Status = Models.OrderStatus.Delivered;
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
                        OrderId = delivery.OrderId,
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
                        $"Se ingresaron {amountCollected:C} del pedido #{delivery.Order.Id} ({delivery.Order.Client?.Name})",
                        tag: "payment-received"
                    );
                }
            }

            // -----------------------------------------------------------
            // 🎀 LÓGICA DE REGIPUNTOS (10 pts por cada $100)
            // -----------------------------------------------------------
            int puntosGanados = (int)(delivery.Order.Total / 10m);
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

        if (delivery.Order.ClientId > 0)
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

        var delivery = await _db.Deliveries.Include(d => d.Order)
            .FirstOrDefaultAsync(d => d.Id == deliveryId && d.DeliveryRouteId == route.Id);
        if (delivery == null) return NotFound();

        delivery.Status = DeliveryStatus.NotDelivered;
        delivery.FailureReason = req.Reason;
        delivery.Notes = req.Notes;
        delivery.Order.Status = Models.OrderStatus.NotDelivered;

        if (photos != null) await SavePhotos(delivery, photos, EvidenceType.NonDeliveryProof);

        await _db.SaveChangesAsync();
        
        await _push.SendNotificationToAdminsAsync(
            "⚠️ Entrega Fallida",
            $"{delivery.Order.Client?.Name ?? "Cliente"} no recibió el pedido: {req.Reason}",
            tag: "delivery-failed"
        );

        // Notificar Admin Dashboard
        await _hub.Clients.Group($"Route_{driverToken}").SendAsync("DeliveryStatusUpdate", new { delivery.Id, Status = "NotDelivered" });
        await _hub.Clients.Group("Admins").SendAsync("DeliveryUpdate", new { 
            OrderId = delivery.Order.Id, 
            Status = "NotDelivered",
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
            await _hub.Clients.Group($"Order_{nextDelivery.Order.AccessToken}")
                .SendAsync("DeliveryUpdate", new { Status = "InTransit", Message = "¡Tu turno! El repartidor va hacia ti." });
                
            if (nextDelivery.Order.ClientId > 0)
                await _push.NotifyClientDriverEnRouteAsync(nextDelivery.Order.ClientId);

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

        route.Status = RouteStatus.Completed;
        route.CompletedAt = DateTime.UtcNow;

        // ── Auto-conciliación de bolsas sin resolver ──
        // Carga todos los paquetes que aún estén en estado Loaded (ni entregados ni devueltos)
        var loadedPackages = await _db.OrderPackages
            .Include(p => p.Order)
                .ThenInclude(o => o.Delivery)
            .Where(p => p.Status == PackageTrackingStatus.Loaded
                && p.Order.Delivery != null
                && p.Order.Delivery.DeliveryRouteId == routeId)
            .ToListAsync();

        int autoReturned = 0;
        foreach (var pkg in loadedPackages)
        {
            var deliveryStatus = pkg.Order.Delivery!.Status;
            if (deliveryStatus == DeliveryStatus.Delivered)
            {
                pkg.Status = PackageTrackingStatus.Delivered;
                pkg.DeliveredAt = pkg.Order.Delivery.DeliveredAt ?? DateTime.UtcNow;
            }
            else
            {
                // NotDelivered o cualquier otro estado terminal → devuelta
                pkg.Status = PackageTrackingStatus.Returned;
                pkg.ReturnedAt = DateTime.UtcNow;
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