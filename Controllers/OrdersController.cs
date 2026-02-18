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
public class OrdersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IExcelService _excelService;
    private readonly ITokenService _tokenService;
    private readonly IConfiguration _config;

    public OrdersController(AppDbContext db, IExcelService excelService,
        ITokenService tokenService, IConfiguration config)
    {
        _db = db;
        _excelService = excelService;
        _tokenService = tokenService;
        _config = config;
    }

    private string FrontendUrl => _config["App:FrontendUrl"] ?? "https://regibazar.com";

    // ---------------------------------------------------------
    // GET & UPLOAD (SIN CAMBIOS)
    // ---------------------------------------------------------

    [HttpGet]
    public async Task<ActionResult<List<OrderSummaryDto>>> GetAll()
    {
        var orders = await _db.Orders
            .Include(o => o.Client)
            .Include(o => o.Items)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync();

        return Ok(orders.Select(o =>
            ExcelService.MapToSummary(o, o.Client, FrontendUrl)).ToList());
    }

    [HttpPost("upload")]
    public async Task<ActionResult<ExcelUploadResult>> UploadExcel(IFormFile file)
    {
        if (file == null || file.Length == 0) return BadRequest("No se recibió archivo.");
        var ext = Path.GetExtension(file.FileName).ToLower();
        if (ext != ".xlsx" && ext != ".xls" && ext != ".xlsm") return BadRequest("Solo Excel (.xlsx, .xls).");

        using var stream = file.OpenReadStream();
        var result = await _excelService.ProcessExcelAsync(stream, FrontendUrl);
        return Ok(result);
    }

    // ---------------------------------------------------------
    // CREATE MANUAL (CON LÓGICA DE FUSIÓN CORREGIDA) 🧠✨
    // ---------------------------------------------------------

    /// <summary>POST /api/orders/manual - Crea o FUSIONA pedidos pendientes</summary>
    [HttpPost("manual")]
    public async Task<ActionResult<OrderSummaryDto>> CreateManual(ManualOrderRequest req)
    {
        var settings = await _db.AppSettings.FirstAsync();

        // 1. Buscamos o Creamos al Cliente
        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Name.ToLower() == req.ClientName.Trim().ToLower());

        if (client == null)
        {
            client = new Client
            {
                Name = req.ClientName.Trim(),
                Phone = req.ClientPhone,
                Address = req.ClientAddress,
                Type = req.ClientType ?? "Nueva",
                CreatedAt = DateTime.UtcNow
            };
            _db.Clients.Add(client);
            await _db.SaveChangesAsync(); // Guardamos para tener el ID
        }
        else
        {
            // Actualizamos datos de contacto si vienen nuevos
            if (!string.IsNullOrEmpty(req.ClientPhone)) client.Phone = req.ClientPhone;
            if (!string.IsNullOrEmpty(req.ClientAddress)) client.Address = req.ClientAddress;
            if (!string.IsNullOrEmpty(req.ClientType)) client.Type = req.ClientType;
        }

        // 2. BUSCAMOS SI YA TIENE UN PEDIDO ABIERTO (PENDIENTE) 🕵️‍♂️
        var existingOrder = await _db.Orders
            .Include(o => o.Items) // Importante traer los items para no borrarlos
            .FirstOrDefaultAsync(o => o.ClientId == client.Id
                                   && o.Status == Models.OrderStatus.Pending);

        // Parseamos el tipo de orden que viene del request
        Enum.TryParse<OrderType>(req.OrderType, true, out var reqOrderType);

        if (existingOrder != null)
        {
            // --- FUSIÓN (MERGE) ---
            // Ya existe un pedido abierto, le agregamos lo nuevo.

            // Actualizamos al tipo más reciente
            existingOrder.OrderType = reqOrderType;
            existingOrder.ShippingCost = (reqOrderType == OrderType.PickUp) ? 0 : settings.DefaultShippingCost;

            // Agregamos los items nuevos a la lista existente
            foreach (var item in req.Items)
            {
                var lineTotal = item.UnitPrice * item.Quantity;
                existingOrder.Items.Add(new OrderItem
                {
                    ProductName = item.ProductName,
                    Quantity = item.Quantity,
                    UnitPrice = item.UnitPrice,
                    LineTotal = lineTotal
                });
            }

            // Recalculamos totales sumando lo viejo + lo nuevo
            existingOrder.Subtotal = existingOrder.Items.Sum(i => i.LineTotal);
            existingOrder.Total = existingOrder.Subtotal + existingOrder.ShippingCost;

            // Actualizamos fecha para que suba en la lista (opcional, para que se vea reciente)
            existingOrder.CreatedAt = DateTime.UtcNow;
        }
        else
        {
            // --- CREACIÓN NUEVA ---
            // No tiene pendientes, creamos uno nuevo.

            var accessToken = _tokenService.GenerateAccessToken();
            var newOrder = new Order
            {
                ClientId = client.Id,
                AccessToken = accessToken,
                ShippingCost = (reqOrderType == OrderType.PickUp) ? 0 : settings.DefaultShippingCost,
                ExpiresAt = DateTime.UtcNow.AddHours(settings.LinkExpirationHours),
                Status = Models.OrderStatus.Pending,
                OrderType = reqOrderType,
                Items = new List<OrderItem>(),
                CreatedAt = DateTime.UtcNow
            };

            foreach (var item in req.Items)
            {
                var lineTotal = item.UnitPrice * item.Quantity;
                newOrder.Items.Add(new OrderItem
                {
                    ProductName = item.ProductName,
                    Quantity = item.Quantity,
                    UnitPrice = item.UnitPrice,
                    LineTotal = lineTotal
                });
            }

            newOrder.Subtotal = newOrder.Items.Sum(i => i.LineTotal);
            newOrder.Total = newOrder.Subtotal + newOrder.ShippingCost;

            _db.Orders.Add(newOrder);
            existingOrder = newOrder; // Referencia para el return
        }

        await _db.SaveChangesAsync();

        return Ok(ExcelService.MapToSummary(existingOrder, client, FrontendUrl));
    }

    // ---------------------------------------------------------
    // ACTUALIZACIONES, BORRADO Y OTROS
    // ---------------------------------------------------------

    /// <summary>DELETE /api/orders/{id} - Borrado inteligente</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var order = await _db.Orders.FindAsync(id);
        if (order == null) return NotFound();

        // Usamos 'Delivery' (tu modelo) en lugar de 'RouteDelivery'
        var delivery = await _db.Deliveries
            .Include(d => d.DeliveryRoute) // Tu propiedad de navegación es 'DeliveryRoute'
            .FirstOrDefaultAsync(d => d.OrderId == id);

        if (delivery != null)
        {
            // Contamos cuántas entregas quedan en esa ruta
            var deliveriesInRoute = await _db.Deliveries
                .CountAsync(d => d.DeliveryRouteId == delivery.DeliveryRouteId);

            // Borramos la entrega de este pedido
            _db.Deliveries.Remove(delivery);

            // Si era la única entrega, borramos la ruta padre
            if (deliveriesInRoute <= 1)
            {
                _db.DeliveryRoutes.Remove(delivery.DeliveryRoute);
            }
        }

        _db.Orders.Remove(order);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>DELETE /api/orders/{orderId}/items/{itemId}</summary>
    [HttpDelete("{orderId}/items/{itemId}")]
    public async Task<ActionResult<OrderSummaryDto>> RemoveItem(int orderId, int itemId)
    {
        var order = await _db.Orders
            .Include(o => o.Items)
            .Include(o => o.Client)
            .FirstOrDefaultAsync(o => o.Id == orderId);

        if (order == null) return NotFound("Pedido no encontrado 😿");

        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null) return NotFound("El artículo no está en este pedido 🤷‍♀️");

        order.Items.Remove(item);

        if (order.Items.Any())
        {
            order.Subtotal = order.Items.Sum(i => i.LineTotal);
            order.Total = order.Subtotal + order.ShippingCost;
        }
        else
        {
            order.Subtotal = 0;
            order.Total = 0;
            if (order.Status != Models.OrderStatus.Delivered) order.Status = Models.OrderStatus.Pending;
        }

        await _db.SaveChangesAsync();
        return Ok(ExcelService.MapToSummary(order, order.Client, FrontendUrl));
    }

    // ... [GET Dashboard SE QUEDA IGUAL] ...
    [HttpGet("dashboard")]
    public async Task<ActionResult<DashboardDto>> Dashboard()
    {
        var dto = new DashboardDto(
            TotalClients: await _db.Clients.CountAsync(),
            TotalOrders: await _db.Orders.CountAsync(),
            PendingOrders: await _db.Orders.CountAsync(o => o.Status == Models.OrderStatus.Pending),
            DeliveredOrders: await _db.Orders.CountAsync(o => o.Status == Models.OrderStatus.Delivered),
            NotDeliveredOrders: await _db.Orders.CountAsync(o => o.Status == Models.OrderStatus.NotDelivered),
            ActiveRoutes: await _db.DeliveryRoutes.CountAsync(r => r.Status == RouteStatus.Active),
            TotalRevenue: await _db.Orders
                .Where(o => o.Status == Models.OrderStatus.Delivered)
                .SumAsync(o => o.Total)
        );
        return Ok(dto);
    }

    // ---------------------------------------------------------
    // ENDPOINTS DE TRACKING Y ACTUALIZACIÓN
    // ---------------------------------------------------------


    [HttpPatch("{id}/status")]
    public async Task<ActionResult<OrderSummaryDto>> UpdateStatus(int id, [FromBody] UpdateOrderStatusRequest req)
    {
        if (req == null) return BadRequest("No llegaron datos 😿");

        var order = await _db.Orders
            .Include(o => o.Items)
            .Include(o => o.Client)
            .FirstOrDefaultAsync(o => o.Id == id);

        if (order == null) return NotFound("Orden no encontrada");

        var settings = await _db.AppSettings.FirstAsync();

        if (Enum.TryParse<OrderType>(req.OrderType, true, out var newType))
        {
            if (order.OrderType != newType)
            {
                order.OrderType = newType;

                if (newType == OrderType.PickUp)
                {
                    order.ShippingCost = 0;
                    // Lógica para sacar de ruta si estaba asignado...
                    var delivery = await _db.Deliveries
                        .Include(d => d.DeliveryRoute)
                        .FirstOrDefaultAsync(d => d.OrderId == id);

                    if (delivery != null)
                    {
                        var deliveriesInRoute = await _db.Deliveries
                            .CountAsync(d => d.DeliveryRouteId == delivery.DeliveryRouteId);
                        _db.Deliveries.Remove(delivery);
                        if (deliveriesInRoute <= 1)
                        {
                            // Opcional: Borrar ruta vacía
                        }
                    }
                }
                else
                {
                    order.ShippingCost = settings.DefaultShippingCost;
                }
            }
        }

        if (Enum.TryParse<Models.OrderStatus>(req.Status, true, out var newStatus))
        {
            order.Status = newStatus;
        }

        order.PostponedAt = req.PostponedAt;
        order.PostponedNote = req.PostponedNote;
        order.Total = order.Subtotal + order.ShippingCost;

        await _db.SaveChangesAsync();

        return Ok(ExcelService.MapToSummary(order, order.Client, FrontendUrl));
    }

    // -----------------------------------------------------------------------
    // NUEVOS ENDPOINTS (EDICIÓN TOTAL)
    // -----------------------------------------------------------------------

    /// <summary>PUT /api/orders/{id} - Actualiza detalles, cliente y estado</summary>
    [HttpPut("{id}")]
    public async Task<ActionResult<OrderSummaryDto>> UpdateOrderDetails(int id, [FromBody] UpdateOrderDetailsRequest req)
    {
        var order = await _db.Orders
            .Include(o => o.Client)
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == id);

        if (order == null) return NotFound("Orden no encontrada");

        // Actualizamos datos del Cliente
        if (order.Client != null)
        {
            order.Client.Name = req.ClientName;
            order.Client.Address = req.ClientAddress;
            order.Client.Phone = req.ClientPhone;
        }

        var settings = await _db.AppSettings.FirstAsync();

        // Lógica de Tipo de Orden
        if (Enum.TryParse<OrderType>(req.OrderType, true, out var newType))
        {
            if (order.OrderType != newType)
            {
                order.OrderType = newType;
                if (newType == OrderType.PickUp) order.ShippingCost = 0;
                else order.ShippingCost = settings.DefaultShippingCost;
            }
        }

        if (Enum.TryParse<Models.OrderStatus>(req.Status, true, out var newStatus))
        {
            order.Status = newStatus;
        }

        order.PostponedAt = req.PostponedAt;
        order.PostponedNote = req.PostponedNote;
        order.Total = order.Subtotal + order.ShippingCost;

        await _db.SaveChangesAsync();

        return Ok(ExcelService.MapToSummary(order, order.Client, FrontendUrl));
    }

    /// <summary>PUT /api/orders/{orderId}/items/{itemId} - Edita un producto específico</summary>
    [HttpPut("{orderId}/items/{itemId}")]
    public async Task<ActionResult<OrderSummaryDto>> UpdateOrderItem(int orderId, int itemId, [FromBody] UpdateOrderItemRequest req)
    {
        var order = await _db.Orders
            .Include(o => o.Items)
            .Include(o => o.Client)
            .FirstOrDefaultAsync(o => o.Id == orderId);

        if (order == null) return NotFound("Orden no encontrada");

        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null) return NotFound("El producto no existe en esta orden");

        item.ProductName = req.ProductName;
        item.Quantity = req.Quantity;
        item.UnitPrice = req.UnitPrice;
        item.LineTotal = item.Quantity * item.UnitPrice;

        order.Subtotal = order.Items.Sum(i => i.LineTotal);
        order.Total = order.Subtotal + order.ShippingCost;

        await _db.SaveChangesAsync();

        return Ok(ExcelService.MapToSummary(order, order.Client, FrontendUrl));
    }

    // -----------------------------------------------------------------------
    // ENDPOINT DE LIMPIEZA DE DUPLICADOS (ADMIN)
    // -----------------------------------------------------------------------

    [HttpPost("admin/merge-duplicates")]
    public async Task<IActionResult> FixDuplicates()
    {
        // 1. Traemos TODOS los pedidos pendientes con sus clientes e items
        var allPending = await _db.Orders
            .Include(o => o.Client)
            .Include(o => o.Items)
            .Where(o => o.Status == Models.OrderStatus.Pending)
            .ToListAsync();

        // 2. Agrupamos por NOMBRE de clienta (limpiando espacios y mayúsculas)
        //    Así "Juana Perez" y "juana perez " serán la misma persona.
        var groupedByName = allPending
            .GroupBy(o => o.Client.Name.Trim().ToLower())
            .Where(g => g.Count() > 1) // Solo las que tengan duplicados
            .ToList();

        int fixedCount = 0;

        foreach (var group in groupedByName)
        {
            // Ordenamos por fecha: La más vieja será la "Maestra"
            var orders = group.OrderBy(o => o.CreatedAt).ToList();
            var masterOrder = orders.First();
            var duplicates = orders.Skip(1).ToList();

            foreach (var dup in duplicates)
            {
                // Mover items del duplicado al maestro
                foreach (var item in dup.Items.ToList()) // ToList para evitar error de modificación
                {
                    // Desligamos del pedido viejo y asignamos al nuevo
                    item.OrderId = masterOrder.Id;
                    masterOrder.Items.Add(item);
                }

                // Borramos el pedido duplicado
                _db.Orders.Remove(dup);

                // Opcional: Si el duplicado tenía un cliente diferente al maestro,
                // ese cliente se quedará "huérfano" (sin pedidos).
                // Podríamos borrarlo aquí, pero es más seguro dejarlo y limpiarlo luego.
            }

            // Recalcular totales
            masterOrder.Subtotal = masterOrder.Items.Sum(i => i.LineTotal);
            masterOrder.Total = masterOrder.Subtotal + masterOrder.ShippingCost;

            // Actualizar fecha para que suba en la lista
            masterOrder.CreatedAt = DateTime.UtcNow;

            fixedCount++;
        }

        await _db.SaveChangesAsync();
        return Ok($"¡Listo! Se fusionaron pedidos duplicados de {fixedCount} clientas por Nombre.");
    }

    // ---------------------------------------------------------
    // 🕵️‍♂️ ENDPOINT DE DIAGNÓSTICO (SOLO PARA VER QUÉ PASA)
    // ---------------------------------------------------------

    [HttpGet("debug/pending")]
    public async Task<IActionResult> DebugPendingOrders()
    {
        // 1. Traer TODOS los pedidos que NO han sido entregados ni cancelados
        var activeOrders = await _db.Orders
            .Include(o => o.Client)
            .Where(o => o.Status == Models.OrderStatus.Pending)
            .OrderBy(o => o.Client.Name)
            .Select(o => new
            {
                OrderId = o.Id,
                ClientNameRaw = o.Client.Name, // Nombre tal cual está en DB
                ClientNameNormalized = o.Client.Name.Trim().ToLower(), // Nombre como lo compara el fusionador
                o.Status,
                o.CreatedAt,
                o.Total,
                ItemsCount = o.Items.Count
            })
            .ToListAsync();

        // 2. Agruparlos nosotros mismos para ver quién tiene más de 1
        var duplicates = activeOrders
            .GroupBy(x => x.ClientNameNormalized)
            .Where(g => g.Count() > 1)
            .Select(g => new
            {
                Name = g.Key,
                Count = g.Count(),
                Orders = g.ToList()
            })
            .ToList();

        return Ok(new
        {
            Message = $"Hay {activeOrders.Count} pedidos pendientes en total.",
            PotentialDuplicates = duplicates.Count, // Si esto es 0, es que no hay nada que fusionar
            DuplicateDetails = duplicates,
            AllPendingList = activeOrders // Aquí verás la lista completa
        });
    }

    [HttpDelete("wipe")]
    // [Authorize(Roles = "Admin")]
    public async Task<IActionResult> WipeAllOrders()
    {
        await _db.OrderItems.ExecuteDeleteAsync();
        await _db.Deliveries.ExecuteDeleteAsync();
        await _db.Orders.ExecuteDeleteAsync();
        return NoContent();
    }
}