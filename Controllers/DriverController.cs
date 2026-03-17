using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Hubs; // <--- Importante
using EntregasApi.Models;
using EntregasApi.Services;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/driver/{driverToken}")]
public class DriverController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IHubContext<DeliveryHub> _hub;
    private readonly IWebHostEnvironment _env;
    private readonly IPushNotificationService _push;
    private readonly ICamiService _cami;

    public DriverController(AppDbContext db, IHubContext<DeliveryHub> hub, IWebHostEnvironment env,
        IPushNotificationService push, ICamiService cami)
    {
        _db = db;
        _hub = hub;
        _env = env;
        _push = push;
        _cami = cami;
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
                EvidenceUrls: d.Evidences.Select(e => $"/uploads/{e.ImagePath}").ToList(),
                ClientPhone: d.Order.Client.Phone,
                PaymentMethod: d.Order.PaymentMethod,
                Payments: (d.Order.Payments ?? new List<OrderPayment>())
                    .Select(p => new OrderPaymentDto(p.Id, p.OrderId, p.Amount, p.Method, p.Date, p.RegisteredBy, p.Notes)).ToList(),
                Items: (d.Order.Items ?? new List<OrderItem>())
                    .Select(i => new OrderItemDto(i.Id, i.ProductName, i.Quantity, i.UnitPrice, i.LineTotal)).ToList(),
                AmountPaid: d.Order.AmountPaid,
                BalanceDue: d.Order.BalanceDue
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
        await _db.SaveChangesAsync();

        // Notificaciones
        await _hub.Clients.Group($"Order_{delivery.Order.AccessToken}")
            .SendAsync("DeliveryUpdate", new { Status = "InTransit", Message = "¡El repartidor va en camino hacia ti!" });
        
        if (delivery.Order.ClientId > 0)
            await _push.NotifyClientDriverEnRouteAsync(delivery.Order.ClientId);

        // Admin
        await _hub.Clients.Group($"Route_{driverToken}")
             .SendAsync("DeliveryStatusUpdate", new { delivery.Id, Status = "InTransit" });

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

    [HttpPost("location")]
    public async Task<IActionResult> UpdateLocation(string driverToken, UpdateLocationRequest req)
    {
        var route = await _db.DeliveryRoutes.FirstOrDefaultAsync(r => r.DriverToken == driverToken);
        if (route == null) return NotFound();

        route.CurrentLatitude = req.Latitude;
        route.CurrentLongitude = req.Longitude;
        route.LastLocationUpdate = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Notificar al Admin en tiempo real
        await _hub.Clients.Group($"Route_{driverToken}")
             .SendAsync("ReceiveLocation", route.Id, req.Latitude, req.Longitude);

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
        if (delivery.Status != DeliveryStatus.Delivered)
        {
            delivery.Status = DeliveryStatus.Delivered;
            delivery.DeliveredAt = DateTime.UtcNow;
            delivery.Notes = req.Notes;
            delivery.Order.Status = Models.OrderStatus.Delivered;

            // Deserializar pagos desde JSON crudo en FormData
            List<PaymentInputDto>? parsedPayments = null;
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

        var nextId = await AutoAdvanceToNext(route.Id, delivery.SortOrder);
        await CheckRouteCompletion(route.Id);

        return Ok(new { message = "Entrega registrada y puntos asignados.", nextDeliveryId = nextId });
    }

    [HttpPost("fail/{deliveryId}")]
    public async Task<IActionResult> MarkFailed(string driverToken, int deliveryId,
        [FromForm] FailDeliveryRequest req, [FromForm] List<IFormFile>? photos)
    {
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

        // Notificar Admin
        await _hub.Clients.Group($"Route_{driverToken}").SendAsync("DeliveryStatusUpdate", new { delivery.Id, Status = "NotDelivered" });

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
        var uploadDir = Path.Combine(_env.ContentRootPath, "uploads", "evidence");
        Directory.CreateDirectory(uploadDir);

        foreach (var photo in photos.Where(p => p.Length > 0))
        {
            var fileName = $"{delivery.Id}_{Guid.NewGuid():N}{Path.GetExtension(photo.FileName)}";
            using var stream = new FileStream(Path.Combine(uploadDir, fileName), FileMode.Create);
            await photo.CopyToAsync(stream);
            _db.DeliveryEvidences.Add(new DeliveryEvidence { DeliveryId = delivery.Id, ImagePath = $"evidence/{fileName}", Type = type });
        }
    }

    private async Task CheckRouteCompletion(int routeId)
    {
        var allDone = !await _db.Deliveries.AnyAsync(d => d.DeliveryRouteId == routeId && (d.Status == DeliveryStatus.Pending || d.Status == DeliveryStatus.InTransit));
        if (allDone)
        {
            var route = await _db.DeliveryRoutes.FindAsync(routeId);
            if (route != null)
            {
                route.Status = RouteStatus.Completed;
                route.CompletedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }
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
            var fileName = $"r{route.Id}_{Guid.NewGuid():N}{Path.GetExtension(photo.FileName)}";
            var uploadDir = Path.Combine(_env.ContentRootPath, "uploads", "expenses");
            Directory.CreateDirectory(uploadDir);
            using var stream = new FileStream(Path.Combine(uploadDir, fileName), FileMode.Create);
            await photo.CopyToAsync(stream);
            expense.EvidencePath = $"expenses/{fileName}";
        }

        _db.DriverExpenses.Add(expense);
        await _db.SaveChangesAsync();
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