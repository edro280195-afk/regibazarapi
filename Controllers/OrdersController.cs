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

    private string FrontendUrl => _config["App:FrontendUrl"] ?? "http://localhost:4200";

    // ... [GET GetAll y POST UploadExcel SE QUEDAN IGUAL] ...
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

    /// <summary>POST /api/orders/manual - Crea orden manualmente (con fusión)</summary>
    [HttpPost("manual")]
    public async Task<ActionResult<OrderSummaryDto>> CreateManual(ManualOrderRequest req)
    {
        var settings = await _db.AppSettings.FirstAsync();

        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Name.ToLower() == req.ClientName.ToLower());

        if (client == null)
        {
            client = new Client { Name = req.ClientName, Phone = req.ClientPhone, Address = req.ClientAddress, Type = req.ClientType ?? "Nueva" };
            _db.Clients.Add(client);
            await _db.SaveChangesAsync();
        }
        else
        {
            if (!string.IsNullOrEmpty(req.ClientPhone)) client.Phone = req.ClientPhone;
            if (!string.IsNullOrEmpty(req.ClientAddress)) client.Address = req.ClientAddress;
            if (!string.IsNullOrEmpty(req.ClientType)) client.Type = req.ClientType;
        }

        var order = await _db.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.ClientId == client.Id && o.Status == Models.OrderStatus.Pending);

        // Determinamos el tipo de orden desde el request (string a Enum)
        Enum.TryParse<OrderType>(req.OrderType, true, out var orderType);

        if (order == null)
        {
            var accessToken = _tokenService.GenerateAccessToken();
            order = new Order
            {
                ClientId = client.Id,
                AccessToken = accessToken,
                // Regla: PickUp = Envío Gratis ($0)
                ShippingCost = (orderType == OrderType.PickUp) ? 0 : settings.DefaultShippingCost,
                ExpiresAt = DateTime.UtcNow.AddHours(settings.LinkExpirationHours),
                Status = Models.OrderStatus.Pending,
                OrderType = orderType, // Guardamos el tipo
                Items = new List<OrderItem>()
            };
            _db.Orders.Add(order);
        }
        else
        {
            // Si ya existe y es manual, podemos actualizar el tipo si la clienta cambió de opinión
            // Ejemplo: Era Delivery pero ahora dice "paso por el", cambiamos a PickUp y quitamos costo
            if (orderType == OrderType.PickUp)
            {
                order.OrderType = OrderType.PickUp;
                order.ShippingCost = 0;
            }
            // Si era PickUp y ahora pide Delivery, podríamos cobrar envío (lógica opcional, por ahora lo dejamos simple)
        }

        foreach (var item in req.Items)
        {
            var lineTotal = item.UnitPrice * item.Quantity;
            order.Items.Add(new OrderItem
            {
                ProductName = item.ProductName,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice,
                LineTotal = lineTotal
            });
        }

        decimal subtotal = order.Items.Sum(i => i.LineTotal);
        order.Subtotal = subtotal;
        order.Total = subtotal + order.ShippingCost;

        await _db.SaveChangesAsync();

        return Ok(ExcelService.MapToSummary(order, client, FrontendUrl));
    }

    // ---------------------------------------------------------
    // AQUÍ ESTÁ EL CAMBIO IMPORTANTE USANDO TU CLASE 'Delivery'
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

    [HttpPatch("{id}/status")]
    public async Task<ActionResult<OrderSummaryDto>> UpdateStatus(int id, [FromBody] UpdateOrderStatusRequest req)
    {
        // 1. Evitar el 500 por Body vacío
        if (req == null) return BadRequest("No llegaron datos 😿");

        var order = await _db.Orders
            .Include(o => o.Items)
            .Include(o => o.Client)
            .FirstOrDefaultAsync(o => o.Id == id);

        if (order == null) return NotFound("Orden no encontrada");

        var settings = await _db.AppSettings.FirstAsync();

        // 2. Lógica de Cambio de Tipo (Delivery <-> PickUp)
        if (Enum.TryParse<OrderType>(req.OrderType, true, out var newType))
        {
            // Solo si hubo cambio de tipo
            if (order.OrderType != newType)
            {
                order.OrderType = newType;

                // Si ahora es PICKUP (Pasar a recoger)
                if (newType == OrderType.PickUp)
                {
                    order.ShippingCost = 0; // Envío gratis

                    // --- INICIO LÓGICA: SACAR DE RUTA ---
                    var delivery = await _db.Deliveries
                        .Include(d => d.DeliveryRoute)
                        .FirstOrDefaultAsync(d => d.OrderId == id);

                    if (delivery != null)
                    {
                        // Checamos si la ruta se va a quedar vacía
                        var deliveriesInRoute = await _db.Deliveries
                            .CountAsync(d => d.DeliveryRouteId == delivery.DeliveryRouteId);

                        // Borramos la entrega
                        _db.Deliveries.Remove(delivery);

                        // Si era la única entrega de esa ruta, borramos la ruta entera para no dejar basura
                        if (deliveriesInRoute <= 1)
                        {
                            // Opcional: Solo si quieres borrar la ruta si se queda sin pedidos
                            // _db.DeliveryRoutes.Remove(delivery.DeliveryRoute); 
                        }
                    }
                }
                else
                {
                    // Si cambió a Delivery, le cobramos el envío default
                    order.ShippingCost = settings.DefaultShippingCost;
                }
            }
        }

        // 3. Actualizar Status
        if (Enum.TryParse<Models.OrderStatus>(req.Status, true, out var newStatus))
        {
            order.Status = newStatus;
        }

        // 4. Actualizar Datos de Posponer
        order.PostponedAt = req.PostponedAt;
        order.PostponedNote = req.PostponedNote;

        // 5. Recalcular Total Final
        order.Total = order.Subtotal + order.ShippingCost;

        await _db.SaveChangesAsync();

        return Ok(ExcelService.MapToSummary(order, order.Client, FrontendUrl));
    }

    [HttpDelete("wipe")]
    // [Authorize(Roles = "Admin")] // <--- Recomendado si tienes roles
    public async Task<IActionResult> WipeAllOrders()
    {
        // 1. Borramos el detalle de los productos de todas las órdenes
        await _db.OrderItems.ExecuteDeleteAsync();

        // 2. Borramos las entregas vinculadas (Tabla Deliveries)
        // Nota: Esto no borra las Rutas (DeliveryRoutes), solo las asignaciones de pedidos
        await _db.Deliveries.ExecuteDeleteAsync();

        // 3. Finalmente borramos las cabeceras de las órdenes
        await _db.Orders.ExecuteDeleteAsync();

        return NoContent();
    }
}