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
    private readonly IPushNotificationService _pushService;
    private readonly string FrontendUrl;

    public OrdersController(AppDbContext db, IExcelService excelService,
        ITokenService tokenService, IConfiguration config, IPushNotificationService pushService)
    {
        _db = db;
        _excelService = excelService;
        _tokenService = tokenService;
        _config = config;
        _pushService = pushService;
        FrontendUrl = config["App:FrontendUrl"] ?? "https://regibazar.com";
    }

    // ---------------------------------------------------------
    // GET & UPLOAD (SIN CAMBIOS)
    // ---------------------------------------------------------

    [HttpGet]
    public async Task<ActionResult<List<OrderSummaryDto>>> GetAll()
    {
        var orders = await _db.Orders
            .Include(o => o.Client)
            .Include(o => o.Items)
            .Include(o => o.Payments)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync();

        return Ok(orders.Select(o =>
            ExcelService.MapToSummary(o, o.Client, FrontendUrl)).ToList());
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<OrderSummaryDto>> GetOrder(int id)
    {
        var order = await _db.Orders
            .Include(o => o.Client)
            .Include(o => o.Items)
            .Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.Id == id);

        if (order == null) return NotFound("Order no encontrada");

        return Ok(ExcelService.MapToSummary(order, order.Client, FrontendUrl));
    }

    [HttpGet("paged")]
    public async Task<ActionResult<PagedResult<OrderSummaryDto>>> GetPaged(
        [FromQuery] int page = 1, 
        [FromQuery] int pageSize = 50, 
        [FromQuery] string search = "",
        [FromQuery] string status = "",
        [FromQuery] string clientType = "")
    {
        var query = _db.Orders
            .Include(o => o.Client)
            .Include(o => o.Items)
            .Include(o => o.Payments)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (status == "Pending")
            {
                // Excluir Delivered, Canceled y Forzar no PickUp (según Requerimiento 1)
                query = query.Where(o => o.Status != EntregasApi.Models.OrderStatus.Delivered 
                                      && o.Status != EntregasApi.Models.OrderStatus.Canceled 
                                      && o.OrderType != DTOs.OrderType.PickUp);
            }
            else if (status == "PaymentPending")
            {
                query = query.Where(o => o.Status == EntregasApi.Models.OrderStatus.Pending || o.Status == EntregasApi.Models.OrderStatus.InRoute);
            }
            else if (Enum.TryParse<EntregasApi.Models.OrderStatus>(status, true, out var parsedStatus))
            {
                query = query.Where(o => o.Status == parsedStatus);
            }
        }

        if (!string.IsNullOrWhiteSpace(clientType))
        {
            query = query.Where(o => o.Client.Type == clientType);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchStr = search.ToLower().Trim();
            // Try to parse search as order ID if it's numeric or #123
            if (searchStr.StartsWith("#") && int.TryParse(searchStr.Substring(1), out int idVal1))
            {
                query = query.Where(o => o.Id == idVal1);
            }
            else if (int.TryParse(searchStr, out int idVal2))
            {
                query = query.Where(o => o.Id == idVal2 || o.Client.Name.ToLower().Contains(searchStr));
            }
            else
            {
                query = query.Where(o => o.Client.Name.ToLower().Contains(searchStr) 
                                      || (o.Client.Phone != null && o.Client.Phone.Contains(searchStr)));
            }
        }

        var total = await query.CountAsync();
        
        var orders = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var items = orders.Select(o => ExcelService.MapToSummary(o, o.Client, FrontendUrl)).ToList();

        return Ok(new PagedResult<OrderSummaryDto>(items, total, page, pageSize));
    }

    [HttpGet("stats")]
    public async Task<ActionResult<OrderStatsDto>> GetOrderStats()
    {
        // 🚀 CORRECCIÓN: Generamos las fechas y las forzamos a ser UTC para que PostgreSQL no truene
        var today = DateTime.SpecifyKind(DateTime.Today, DateTimeKind.Utc);
        var endOfToday = today.AddDays(1).AddTicks(-1);

        var totalOrders = await _db.Orders.CountAsync();
        var pendingOrders = await _db.Orders.CountAsync(o => o.Status == EntregasApi.Models.OrderStatus.Pending);

        var pendingAmount = await _db.Orders
            .Where(o => o.Status == EntregasApi.Models.OrderStatus.Pending || o.Status == EntregasApi.Models.OrderStatus.InRoute)
            .SelectMany(o => o.Items)
            .SumAsync(i => i.Quantity * i.UnitPrice);

        var collectedToday = await _db.Orders
            .Where(o => o.Status == EntregasApi.Models.OrderStatus.Delivered && o.CreatedAt >= today && o.CreatedAt <= endOfToday)
            .SelectMany(o => o.Items)
            .SumAsync(i => i.Quantity * i.UnitPrice);

        return Ok(new OrderStatsDto(totalOrders, pendingOrders, pendingAmount, collectedToday));
    }

    [HttpGet("generate-vapid")]
    [AllowAnonymous]
    public ActionResult GenerateVapid()
    {
        var keys = WebPush.VapidHelper.GenerateVapidKeys();
        return Ok(new { keys.PublicKey, keys.PrivateKey });
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
            // 1. Obtenemos la hora real en Nuevo Laredo (Monterrey)
            var mexicoZone = TimeZoneInfo.FindSystemTimeZoneById("America/Monterrey");
            var mexicoTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, mexicoZone);

            // 2. Calculamos los días que faltan para el próximo Lunes a las 00:00:00
            // En C#, DayOfWeek: Domingo=0, Lunes=1... Sabado=6
            int daysUntilMonday = (8 - (int)mexicoTime.DayOfWeek) % 7;
            if (daysUntilMonday == 0)
            {
                // Si el pedido se está haciendo en Lunes, la caducidad es el LUNES QUE SIGUE (en 7 días)
                daysUntilMonday = 7;
            }

            // 3. Establecemos la fecha base (Próximo lunes a la medianoche exacta 00:00:00)
            DateTime localExpiration = mexicoTime.Date.AddDays(daysUntilMonday);

            // 4. Regla de negocio: Si es frecuente, le regalamos otra semana entera (hasta el 2do domingo)
            if (client.Type == "Frecuente")
            {
                localExpiration = localExpiration.AddDays(7);
            }

            // 5. Regresamos la fecha a UTC para que PostgreSQL sea feliz
            DateTime expirationUtc = TimeZoneInfo.ConvertTimeToUtc(localExpiration, mexicoZone);

            // 6. Creamos la orden con la fecha calculada
            var newOrder = new Order
            {
                ClientId = client.Id,
                AccessToken = accessToken,
                ShippingCost = (reqOrderType == OrderType.PickUp) ? 0 : settings.DefaultShippingCost,
                ExpiresAt = expirationUtc, // 🚀 ¡Magia matemática!
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
            .Include(o => o.Payments)
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

    // ... [GET Dashboard] ...
    [HttpGet("dashboard")]
    public async Task<ActionResult<DashboardDto>> Dashboard()
    {
        var totalInvestment = await _db.Investments.SumAsync(i => i.Amount * (i.ExchangeRate == 0 ? 1 : i.ExchangeRate));

        var deliveredOrders = await _db.Orders
            .Include(o => o.Payments)
            .Where(o => o.Status == Models.OrderStatus.Delivered)
            .ToListAsync();

        // Agregar payments de todas las orders entregadas
        var allPayments = deliveredOrders.SelectMany(o => o.Payments ?? new List<OrderPayment>()).ToList();

        var dto = new DashboardDto(
            TotalClients: await _db.Clients.CountAsync(),
            TotalOrders: await _db.Orders.CountAsync(),
            PendingOrders: await _db.Orders.CountAsync(o => o.Status == Models.OrderStatus.Pending),
            DeliveredOrders: deliveredOrders.Count,
            NotDeliveredOrders: await _db.Orders.CountAsync(o => o.Status == Models.OrderStatus.NotDelivered),
            ActiveRoutes: await _db.DeliveryRoutes.CountAsync(r => r.Status == RouteStatus.Active),
            TotalRevenue: deliveredOrders.Sum(o => o.Total),
            TotalInvestment: totalInvestment,
            TotalCashOrders: allPayments.Count(p => p.Method == "Efectivo"),
            TotalCashAmount: allPayments.Where(p => p.Method == "Efectivo").Sum(p => p.Amount),
            TotalTransferOrders: allPayments.Count(p => p.Method == "Transferencia"),
            TotalTransferAmount: allPayments.Where(p => p.Method == "Transferencia").Sum(p => p.Amount),
            TotalDepositOrders: allPayments.Count(p => p.Method == "Deposito"),
            TotalDepositAmount: allPayments.Where(p => p.Method == "Deposito").Sum(p => p.Amount)
        );
        return Ok(dto);
    }

    /// <summary>GET /api/orders/reports?start=2024-01-01&end=2024-01-31</summary>
    [HttpGet("reports")]
    public async Task<ActionResult<ReportDto>> GetReports([FromQuery] string start, [FromQuery] string end)
    {
        var startDate = DateTime.SpecifyKind(DateTime.Parse(start), DateTimeKind.Utc);
        var endDate = DateTime.SpecifyKind(DateTime.Parse(end).AddDays(1).AddTicks(-1), DateTimeKind.Utc);

        // ── Pedidos del periodo ──
        var orders = await _db.Orders
            .Include(o => o.Items)
            .Include(o => o.Client)
            .Include(o => o.Payments)
            .Where(o => o.CreatedAt >= startDate && o.CreatedAt <= endDate)
            .ToListAsync();

        var deliveredOrders = orders.Where(o => o.Status == Models.OrderStatus.Delivered).ToList();

        // ── Rutas del periodo ──
        var routes = await _db.DeliveryRoutes
            .Where(r => r.CreatedAt >= startDate && r.CreatedAt <= endDate)
            .ToListAsync();

        var totalDeliveries = await _db.Deliveries
            .Where(d => d.DeliveryRoute.CreatedAt >= startDate && d.DeliveryRoute.CreatedAt <= endDate)
            .CountAsync();

        var deliveredCount = await _db.Deliveries
            .Where(d => d.DeliveryRoute.CreatedAt >= startDate && d.DeliveryRoute.CreatedAt <= endDate
                     && d.Status == DeliveryStatus.Delivered)
            .CountAsync();

        // ── Inversiones del periodo ──
        var totalInvestment = await _db.Investments
            .Where(i => i.Date >= startDate && i.Date <= endDate)
            .SumAsync(i => i.Amount * (i.ExchangeRate == 0 ? 1 : i.ExchangeRate));

        // ── Gastos del chofer ──
        var driverExpenses = await _db.DriverExpenses
            .Where(e => e.Date >= startDate && e.Date <= endDate)
            .SumAsync(e => e.Amount);

        // ── Revenue
        var revenue = deliveredOrders.Sum(o => o.Total);

        // ── Top Productos ──
        var topProducts = orders
            .SelectMany(o => o.Items)
            .GroupBy(i => i.ProductName)
            .Select(g => new TopProductDto(g.Key, g.Sum(i => i.Quantity), g.Sum(i => i.LineTotal)))
            .OrderByDescending(p => p.Revenue)
            .Take(10)
            .ToList();

        // ── Pedidos por día ──
        var ordersByDay = orders
            .GroupBy(o => o.CreatedAt.ToString("yyyy-MM-dd"))
            .Select(g => new DailyCountDto(g.Key, g.Count(), g.Sum(o => o.Total)))
            .OrderBy(d => d.Date)
            .ToList();

        // ── Clientas ──
        var clientsInPeriod = orders.Where(o => o.Client != null).Select(o => o.Client!).DistinctBy(c => c.Id).ToList();
        var newClients = clientsInPeriod.Count(c => c.Type == "Nueva" || string.IsNullOrEmpty(c.Type));
        var frequentClients = clientsInPeriod.Count(c => c.Type == "Frecuente");

        var topClients = orders
            .Where(o => o.Client != null && o.Status == Models.OrderStatus.Delivered)
            .GroupBy(o => o.Client!.Name)
            .Select(g => new TopClientDto(g.Key, g.Count(), g.Sum(o => o.Total)))
            .OrderByDescending(c => c.TotalSpent)
            .Take(10)
            .ToList();

        // ── Proveedores ──
        var supplierSummaries = await _db.Investments
            .Include(i => i.Supplier)
            .Where(i => i.Date >= startDate && i.Date <= endDate)
            .GroupBy(i => i.Supplier.Name)
            .Select(g => new SupplierSummaryDto(
                g.Key,
                g.Sum(i => i.Amount * (i.ExchangeRate == 0 ? 1 : i.ExchangeRate)),
                g.Count()
            ))
            .ToListAsync();

        var successRate = totalDeliveries > 0 ? Math.Round((decimal)deliveredCount / totalDeliveries * 100, 1) : 0m;

        var reportPayments = deliveredOrders.SelectMany(o => o.Payments ?? new List<OrderPayment>()).ToList();

        var dto = new ReportDto(
            TotalRevenue: revenue,
            TotalInvestment: totalInvestment,
            TotalExpenses: driverExpenses,
            NetProfit: revenue - totalInvestment - driverExpenses,
            TotalOrders: orders.Count,
            PendingOrders: orders.Count(o => o.Status == Models.OrderStatus.Pending),
            InRouteOrders: orders.Count(o => o.Status == Models.OrderStatus.InRoute),
            DeliveredOrders: deliveredOrders.Count,
            NotDeliveredOrders: orders.Count(o => o.Status == Models.OrderStatus.NotDelivered),
            CanceledOrders: orders.Count(o => o.Status == Models.OrderStatus.Canceled),
            DeliveryOrders: orders.Count(o => o.OrderType == OrderType.Delivery),
            PickUpOrders: orders.Count(o => o.OrderType == OrderType.PickUp),
            AvgTicket: orders.Count > 0 ? Math.Round(orders.Average(o => o.Total), 2) : 0,
            TopProducts: topProducts,
            OrdersByDay: ordersByDay,
            TotalRoutes: routes.Count,
            CompletedRoutes: routes.Count(r => r.Status == RouteStatus.Completed),
            SuccessRate: successRate,
            TotalDriverExpenses: driverExpenses,
            NewClients: newClients,
            FrequentClients: frequentClients,
            ActiveClients: clientsInPeriod.Count,
            TopClients: topClients,
            CashOrders: reportPayments.Count(p => p.Method == "Efectivo"),
            CashAmount: reportPayments.Where(p => p.Method == "Efectivo").Sum(p => p.Amount),
            TransferOrders: reportPayments.Count(p => p.Method == "Transferencia"),
            TransferAmount: reportPayments.Where(p => p.Method == "Transferencia").Sum(p => p.Amount),
            DepositOrders: reportPayments.Count(p => p.Method == "Deposito"),
            DepositAmount: reportPayments.Where(p => p.Method == "Deposito").Sum(p => p.Amount),
            UnassignedPaymentOrders: deliveredOrders.Count(o => !(o.Payments?.Any() ?? false)),
            SupplierSummaries: supplierSummaries
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
            .Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.Id == id);

        if (order == null) return NotFound("Orden no encontrada");

        var settings = await _db.AppSettings.FirstAsync();

        if (Enum.TryParse<Models.OrderStatus>(req.Status, true, out var newStatus))
        {
            int puntosCalculados = (int)(order.Total / 10m);

            // CASO A: Lo acaban de marcar como Entregado (+ PUNTOS)
            if (newStatus == Models.OrderStatus.Delivered && order.Status != Models.OrderStatus.Delivered)
            {
                if (puntosCalculados > 0 && order.Client != null)
                {
                    _db.LoyaltyTransactions.Add(new LoyaltyTransaction
                    {
                        ClientId = order.Client.Id,
                        Points = puntosCalculados,
                        Reason = $"Compra Entregada #{order.Id}",
                        Date = DateTime.UtcNow
                    });

                    order.Client.CurrentPoints += puntosCalculados;
                    order.Client.LifetimePoints += puntosCalculados;
                    order.Client.Type = "Frecuente";
                }
            }
            // CASO B: Estaba Entregado y lo regresaron a Pendiente/Cancelado (- PUNTOS)
            else if (order.Status == Models.OrderStatus.Delivered && newStatus != Models.OrderStatus.Delivered)
            {
                if (puntosCalculados > 0 && order.Client != null)
                {
                    _db.LoyaltyTransactions.Add(new LoyaltyTransaction
                    {
                        ClientId = order.Client.Id,
                        Points = -puntosCalculados, // Puntos Negativos
                        Reason = $"Reversión de estado de pedido #{order.Id}",
                        Date = DateTime.UtcNow
                    });

                    order.Client.CurrentPoints -= puntosCalculados;
                    order.Client.LifetimePoints -= puntosCalculados;
                }
            }

            // Aplicamos el cambio de estatus real a la orden
            order.Status = newStatus;
        }

        order.PostponedAt = req.PostponedAt;
        order.PostponedNote = req.PostponedNote;
        order.Total = order.Subtotal + order.ShippingCost;

        await _db.SaveChangesAsync();

        // Enviar Push Notification basada en el nuevo estado
        if (newStatus == EntregasApi.Models.OrderStatus.Shipped && order.Client != null)
        {
            await _pushService.SendNotificationToClientAsync(
                order.Client.Id, 
                "Tu pedido ha sido empacado 📦", 
                "Tu pedido está listo y empacado para entrega.", 
                $"/o/{order.AccessToken}");
        }
        else if (newStatus == EntregasApi.Models.OrderStatus.Delivered && order.Client != null)
        {
            await _pushService.SendNotificationToClientAsync(
                order.Client.Id, 
                "Pedido Entregado 🌸", 
                "¡Gracias por tu compra en Regi Bazar!", 
                $"/o/{order.AccessToken}");
        }
        else if (newStatus == EntregasApi.Models.OrderStatus.InRoute && order.Client != null)
        {
            await _pushService.SendNotificationToClientAsync(
                order.Client.Id, 
                "Pedido en Ruta ✨", 
                "Tu pedido ha salido y se encuentra en ruta de entrega", 
                $"/o/{order.AccessToken}");
        }

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
            .Include(o => o.Payments)
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

        // Requerimiento 3 y 4: Edición de Shipping Cost y Abono
        if (req.ShippingCost.HasValue) order.ShippingCost = req.ShippingCost.Value;
        if (req.AdvancePayment.HasValue) order.AdvancePayment = req.AdvancePayment.Value;

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
            .Include(o => o.Payments)
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

    /// <summary>POST /api/orders/{orderId}/items - Agrega un producto a un pedido existente</summary>
    [HttpPost("{orderId}/items")]
    public async Task<ActionResult<OrderSummaryDto>> AddOrderItem(int orderId, [FromBody] UpdateOrderItemRequest req)
    {
        var order = await _db.Orders
            .Include(o => o.Items)
            .Include(o => o.Client)
            .Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.Id == orderId);

        if (order == null) return NotFound("Orden no encontrada");

        var newItem = new OrderItem
        {
            ProductName = req.ProductName,
            Quantity = req.Quantity,
            UnitPrice = req.UnitPrice,
            LineTotal = req.Quantity * req.UnitPrice
        };

        order.Items.Add(newItem);
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
    // PAGOS (Libro de Transacciones)
    // ---------------------------------------------------------

    /// <summary>POST /api/orders/{id}/payments - Registra un pago</summary>
    [HttpPost("{id}/payments")]
    public async Task<ActionResult<OrderPaymentDto>> AddPayment(int id, [FromBody] AddPaymentRequest req)
    {
        var order = await _db.Orders
            .Include(o => o.Payments)
            .Include(o => o.Client)
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == id);

        if (order == null) return NotFound("Orden no encontrada");
        if (req.Amount <= 0) return BadRequest("El monto debe ser mayor a 0");

        var payment = new OrderPayment
        {
            OrderId = id,
            Amount = req.Amount,
            Method = req.Method,
            Date = DateTime.UtcNow,
            RegisteredBy = req.RegisteredBy,
            Notes = req.Notes
        };

        _db.OrderPayments.Add(payment);
        await _db.SaveChangesAsync();

        return Ok(new OrderPaymentDto(payment.Id, payment.OrderId, payment.Amount, payment.Method, payment.Date, payment.RegisteredBy, payment.Notes));
    }

    /// <summary>DELETE /api/orders/{orderId}/payments/{paymentId}</summary>
    [HttpDelete("{orderId}/payments/{paymentId}")]
    public async Task<IActionResult> DeletePayment(int orderId, int paymentId)
    {
        var payment = await _db.OrderPayments
            .FirstOrDefaultAsync(p => p.Id == paymentId && p.OrderId == orderId);

        if (payment == null) return NotFound("Pago no encontrado");

        _db.OrderPayments.Remove(payment);
        await _db.SaveChangesAsync();
        return NoContent();
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
        await _db.OrderPayments.ExecuteDeleteAsync();
        await _db.OrderItems.ExecuteDeleteAsync();
        await _db.Deliveries.ExecuteDeleteAsync();
        await _db.Orders.ExecuteDeleteAsync();
        return NoContent();
    }
}