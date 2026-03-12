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
    private readonly IGeminiService _geminiService;
    private readonly string FrontendUrl;

    public OrdersController(AppDbContext db, IExcelService excelService,
        ITokenService tokenService, IConfiguration config, IPushNotificationService pushService,
        IGeminiService geminiService)
    {
        _db = db;
        _excelService = excelService;
        _tokenService = tokenService;
        _config = config;
        _pushService = pushService;
        _geminiService = geminiService;
        FrontendUrl = config["App:FrontendUrl"] ?? "https://regibazar.com";
    }

    // ---------------------------------------------------------
    // AI LIVE PARSING
    // ---------------------------------------------------------

    [HttpPost("parse-live")]
    public async Task<ActionResult<List<AiParsedOrder>>> ParseLiveText([FromBody] ParseLiveRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            return BadRequest("Text is required");

        try
        {
            var orders = await _geminiService.ParseLiveTextAsync(request.Text);
            return Ok(orders);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = $"Error al analizar con IA: {ex.Message}" });
        }
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
        [FromQuery] string clientType = "",
        [FromQuery] DateTime? dateFrom = null,
        [FromQuery] DateTime? dateTo = null,
        [FromQuery] int? clientId = null,
        [FromQuery] string sortBy = "date",
        [FromQuery] string sortDir = "desc")
    {
        var query = _db.Orders
            .Include(o => o.Client)
            .Include(o => o.Items)
            .Include(o => o.Payments)
            .AsQueryable();

        if (clientId.HasValue)
        {
            query = query.Where(o => o.ClientId == clientId.Value);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (Enum.TryParse<EntregasApi.Models.OrderStatus>(status, true, out var parsedStatus))
            {
                query = query.Where(o => o.Status == parsedStatus);
            }
        }

        if (!string.IsNullOrWhiteSpace(clientType))
        {
            query = query.Where(o => o.Client != null && o.Client.Type == clientType);
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
                query = query.Where(o => o.Id == idVal2 || (o.Client != null && o.Client.Name.ToLower().Contains(searchStr)));
            }
            else
            {
                query = query.Where(o => (o.Client != null && o.Client.Name.ToLower().Contains(searchStr)) 
                                      || (o.Client != null && o.Client.Phone != null && o.Client.Phone.Contains(searchStr)));
            }
        }

        if (dateFrom.HasValue)
        {
            var from = DateTime.SpecifyKind(dateFrom.Value.Date, DateTimeKind.Utc);
            query = query.Where(o =>
                o.CreatedAt >= from ||
                (o.PostponedAt != null && o.PostponedAt >= from));
        }

        if (dateTo.HasValue)
        {
            var to = DateTime.SpecifyKind(dateTo.Value.Date.AddDays(1), DateTimeKind.Utc);
            query = query.Where(o =>
                o.CreatedAt < to ||
                (o.PostponedAt != null && o.PostponedAt < to));
        }

        var total = await query.CountAsync();

        IOrderedQueryable<Order> sortedQuery = (sortBy, sortDir) switch
        {
            ("total", "asc")    => query.OrderBy(o => o.Total),
            ("total", _)        => query.OrderByDescending(o => o.Total),
            ("client", "desc")  => query.OrderByDescending(o => o.Client!.Name),
            ("client", _)       => query.OrderBy(o => o.Client!.Name),
            ("date", "asc")     => query.OrderBy(o => o.CreatedAt),
            _                   => query.OrderByDescending(o => o.CreatedAt)
        };

        var orders = await sortedQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var items = orders.Select(o => ExcelService.MapToSummary(o, o.Client, FrontendUrl)).ToList();

        return Ok(new PagedResult<OrderSummaryDto>(items, total, page, pageSize));
    }

    [HttpGet("stats")]
    public async Task<ActionResult<OrderStatsDto>> GetOrderStats()
    {
        var totalOrders = await _db.Orders.CountAsync();

        var pendingOrders = await _db.Orders.CountAsync(o => o.Status == EntregasApi.Models.OrderStatus.Pending);

        // 🚀 CORRECCIÓN: Por Cobrar (Todos los NO entregados/cancelados menos sus abonos)
        var activeOrders = await _db.Orders
            .Include(o => o.Payments) // Vital para que no sea null
            .Where(o => o.Status != Models.OrderStatus.Delivered
                     && o.Status != Models.OrderStatus.Canceled
                     && o.Status != Models.OrderStatus.NotDelivered)
            .ToListAsync(); // Lo traemos a memoria RAM para sumar seguros

        // Hacemos la matemática exacta en C# (Total del pedido - Suma de sus pagos)
        var pendingAmount = activeOrders.Sum(o => o.Total - (o.Payments?.Sum(p => p.Amount) ?? 0));

        // 🚀 Ajustar la Zona Horaria para el Cobrado Hoy (CST México) de manera idéntica al Dashboard
        var mexicoTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time (Mexico)") ?? TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City");
        var nowMexico = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, mexicoTimeZone);
        
        var todayStartMexico = new DateTime(nowMexico.Year, nowMexico.Month, nowMexico.Day, 0, 0, 0);
        var todayEndMexico = todayStartMexico.AddDays(1).AddTicks(-1);
        
        var todayStartUtc = TimeZoneInfo.ConvertTimeToUtc(todayStartMexico, mexicoTimeZone);
        var todayEndUtc = TimeZoneInfo.ConvertTimeToUtc(todayEndMexico, mexicoTimeZone);

        // EXTRAEMOS A MEMORIA RAM PARA EMULAR EXACTAMENTE EL MISMO EFECTO DE DASHBOARD()
        var allPayments = await _db.OrderPayments.ToListAsync();
        var collectedToday = allPayments
            .Where(p => p.Date >= todayStartUtc && p.Date <= todayEndUtc)
            .Sum(p => p.Amount);

        return Ok(new OrderStatsDto(totalOrders, pendingOrders, pendingAmount, collectedToday));
    }

    [HttpGet("generate-vapid")]
    [AllowAnonymous]
    public ActionResult GenerateVapid()
    {
        var keys = WebPush.VapidHelper.GenerateVapidKeys();
        return Ok(new { keys.PublicKey, keys.PrivateKey });
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] DateTime start, [FromQuery] DateTime end)
    {
        var fileBytes = await _excelService.GenerateReportExcelAsync(start, end);
        return File(fileBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Reporte_{start:yyyyMMdd}_{end:yyyyMMdd}.xlsx");
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
                ClientId      = client.Id,
                AccessToken   = accessToken,
                ShippingCost  = (reqOrderType == OrderType.PickUp) ? 0 : settings.DefaultShippingCost,
                ExpiresAt     = expirationUtc,
                Status        = Models.OrderStatus.Pending,
                OrderType     = reqOrderType,
                Items         = new List<OrderItem>(),
                CreatedAt     = DateTime.UtcNow,
                SalesPeriodId = (await _db.SalesPeriods.FirstOrDefaultAsync(p => p.IsActive))?.Id,
                DeliveryInstructions = req.DeliveryInstructions ?? client.DeliveryInstructions
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
            .Where(o => o.Status == Models.OrderStatus.Delivered)
            .ToListAsync();

        // Se debe usar la hora actual (CST para México o basarse en UTC con una resta horaria para evitar traslapes del día siguiente)
        var mexicoTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time (Mexico)") ?? TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City");
        var nowMexico = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, mexicoTimeZone);
        
        var todayStartMexico = new DateTime(nowMexico.Year, nowMexico.Month, nowMexico.Day, 0, 0, 0);
        var todayEndMexico = todayStartMexico.AddDays(1).AddTicks(-1);
        
        var startOfMonthMexico = new DateTime(nowMexico.Year, nowMexico.Month, 1, 0, 0, 0);

        // Convertimos esos límites de regreso a UTC para comparar con la base de datos (que guarda las fechas en UTC)
        var todayStartUtc = TimeZoneInfo.ConvertTimeToUtc(todayStartMexico, mexicoTimeZone);
        var todayEndUtc = TimeZoneInfo.ConvertTimeToUtc(todayEndMexico, mexicoTimeZone);
        var startOfMonthUtc = TimeZoneInfo.ConvertTimeToUtc(startOfMonthMexico, mexicoTimeZone);

        // Get active period for filtering
        var activePeriod = await _db.SalesPeriods.FirstOrDefaultAsync(p => p.IsActive);

        // All payments - Filter by active period if exists for the "Wallet" section
        var queryPayments = _db.OrderPayments.AsQueryable();
        if (activePeriod != null)
        {
            queryPayments = queryPayments.Where(p => p.Order.SalesPeriodId == activePeriod.Id);
        }
        var paymentsForWallet = await queryPayments.ToListAsync();
        
        // Total investment - Also filter by active period if exists
        var investmentToDisplay = activePeriod != null 
            ? await _db.Investments.Where(i => i.SalesPeriodId == activePeriod.Id).SumAsync(i => i.Amount * (i.ExchangeRate == 0 ? 1 : i.ExchangeRate))
            : await _db.Investments.SumAsync(i => i.Amount * (i.ExchangeRate == 0 ? 1 : i.ExchangeRate));

        // Period sales
        var allPayments = await _db.OrderPayments.ToListAsync();
        var revenueToday = allPayments.Where(p => p.Date >= todayStartUtc && p.Date <= todayEndUtc).Sum(p => p.Amount);
        var revenueMonth = allPayments.Where(p => p.Date >= startOfMonthUtc).Sum(p => p.Amount);
        var totalRevenue = paymentsForWallet.Sum(p => p.Amount); // Now represents the active period if exists

        // Chart data
        var sixMonthsAgo = startOfMonthUtc.AddMonths(-5);
        var recentPayments = allPayments.Where(p => p.Date >= sixMonthsAgo).ToList();
        var salesByMonth = recentPayments
            .GroupBy(p => new { p.Date.Year, p.Date.Month })
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .Select(g => new MonthlySalesDto(
                Month: new DateTime(g.Key.Year, g.Key.Month, 1).ToString("MMM", System.Globalization.CultureInfo.GetCultureInfo("es-MX")),
                Sales: g.Sum(p => p.Amount)
            ))
            .ToList();

        // Active Period ROI (Targeting current cycle)
        ActivePeriodSummaryDto? activePeriodSummary = null;
        if (activePeriod != null)
        {
            var periodSales = await _db.Orders
                .Where(o => o.SalesPeriodId == activePeriod.Id && o.Status == Models.OrderStatus.Delivered)
                .SumAsync(o => o.Total);
            
            activePeriodSummary = new ActivePeriodSummaryDto(
                activePeriod.Id,
                activePeriod.Name,
                periodSales,
                investmentToDisplay,
                periodSales - investmentToDisplay,
                totalRevenue
            );
        }

        // Por Cobrar: same logic as GetOrderStats — balance due on active (non-delivered/canceled) orders
        var activeOrdersForPending = await _db.Orders
            .Include(o => o.Payments)
            .Where(o => o.Status != Models.OrderStatus.Delivered
                     && o.Status != Models.OrderStatus.Canceled
                     && o.Status != Models.OrderStatus.NotDelivered)
            .ToListAsync();
        var pendingAmount = activeOrdersForPending.Sum(o => o.Total - (o.Payments?.Sum(p => p.Amount) ?? 0));

        // Recent Activity - 10 most recent orders
        var ordersForDashboard = await _db.Orders
            .Include(o => o.Client)
            .Include(o => o.Items)
            .Include(o => o.Payments)
            .Include(o => o.SalesPeriod)
            .OrderByDescending(o => o.CreatedAt)
            .Take(10)
            .ToListAsync();

        var recentOrders = ordersForDashboard.Select(o => ExcelService.MapToSummary(o, o.Client, FrontendUrl)).ToList();

        var dto = new DashboardDto(
            TotalClients: await _db.Clients.CountAsync(),
            TotalOrders: await _db.Orders.CountAsync(),
            PendingOrders: await _db.Orders.CountAsync(o => o.Status == Models.OrderStatus.Pending),
            DeliveredOrders: deliveredOrders.Count,
            NotDeliveredOrders: await _db.Orders.CountAsync(o => o.Status == Models.OrderStatus.NotDelivered),
            ActiveRoutes: await _db.DeliveryRoutes.CountAsync(r => r.Status == RouteStatus.Active),
            TotalRevenue: totalRevenue,
            RevenueMonth: revenueMonth,
            RevenueToday: revenueToday,
            TotalInvestment: investmentToDisplay,
            TotalCashOrders: paymentsForWallet.Count(p => p.Method == "Efectivo"),
            TotalCashAmount: paymentsForWallet.Where(p => p.Method == "Efectivo").Sum(p => p.Amount),
            TotalTransferOrders: paymentsForWallet.Count(p => p.Method == "Transferencia"),
            TotalTransferAmount: paymentsForWallet.Where(p => p.Method == "Transferencia").Sum(p => p.Amount),
            TotalDepositOrders: paymentsForWallet.Count(p => p.Method == "Deposito"),
            TotalDepositAmount: paymentsForWallet.Where(p => p.Method == "Deposito").Sum(p => p.Amount),
            SalesByMonth: salesByMonth,
            ClientsNueva: await _db.Clients.CountAsync(c => c.Type == "Nueva"),
            ClientsFrecuente: await _db.Clients.CountAsync(c => c.Type == "Frecuente"),
            OrdersDelivery: await _db.Orders.CountAsync(o => o.OrderType == DTOs.OrderType.Delivery),
            OrdersPickUp: await _db.Orders.CountAsync(o => o.OrderType == DTOs.OrderType.PickUp),
            ActivePeriod: activePeriodSummary,
            PendingAmount: pendingAmount,
            RecentOrders: recentOrders
        );
        return Ok(dto);
    }

    /// <summary>GET /api/orders/reports?start=2024-01-01&end=2024-01-31</summary>
    [HttpGet("reports")]
    public async Task<ActionResult<ReportDto>> GetReports([FromQuery] string start, [FromQuery] string end)
    {
        var startDate = DateTime.SpecifyKind(DateTime.Parse(start).Date, DateTimeKind.Utc);
        var endDate = DateTime.SpecifyKind(DateTime.Parse(end).Date, DateTimeKind.Utc);
        var endDateExclusive = endDate.AddDays(1);

        // ── 1. Pedidos involucrados (Creados, Entregados o con Pagos en el rango) ──
        // Para que los totales sean consistentes, traemos pedidos que tengan actividad en el rango
        var orders = await _db.Orders
            .Include(o => o.Items)
            .Include(o => o.Client)
            .Include(o => o.Payments)
            .Where(o => (o.CreatedAt >= startDate && o.CreatedAt < endDateExclusive)
                     || (o.Status == Models.OrderStatus.Delivered && o.Items.Any() && _db.Deliveries.Any(d => d.OrderId == o.Id && d.Status == DeliveryStatus.Delivered && d.DeliveredAt >= startDate && d.DeliveredAt < endDateExclusive))
                     || o.Payments.Any(p => p.Date >= startDate && p.Date < endDateExclusive))
            .ToListAsync();

        // ── 2. Incomes / Billed (Venta Real: Entregas ocurridas en el rango) ──
        // NOTA: Usamos Deliveries para ser ultra-precisos sobre CUANDO se entregó
        var deliveredInPeriodIds = await _db.Deliveries
            .Where(d => d.Status == DeliveryStatus.Delivered && d.DeliveredAt >= startDate && d.DeliveredAt < endDateExclusive)
            .Select(d => d.OrderId)
            .ToListAsync();
        
        // También incluimos orders marcadas como Delivered cuya creación sea en el rango si no tienen registro de Delivery (retro-compatibilidad)
        var deliveredOrders = orders
            .Where(o => o.Status == Models.OrderStatus.Delivered && (deliveredInPeriodIds.Contains(o.Id) || (o.CreatedAt >= startDate && o.CreatedAt < endDateExclusive)))
            .ToList();

        var revenue = deliveredOrders.Sum(o => o.Total);

        // ── 3. Lo Cobrado REAL (OrderPayments con fecha en el rango) ──
        var allPaymentsInRange = await _db.OrderPayments
            .Where(p => p.Date >= startDate && p.Date < endDateExclusive)
            .ToListAsync();
        
        var totalCollected = allPaymentsInRange.Sum(p => p.Amount);

        // ── 4. Inversiones y Gastos (Basados en FECHA) ──
        var totalInvestment = await _db.Investments
            .Where(i => i.Date >= startDate && i.Date < endDateExclusive)
            .SumAsync(i => (decimal?)(i.Amount * i.ExchangeRate)) ?? 0m;

        var driverExpenses = await _db.DriverExpenses
            .Where(e => e.Date >= startDate && e.Date < endDateExclusive)
            .SumAsync(e => (decimal?)e.Amount) ?? 0m;

        // ── 5. Sin asignar (Fugas potenciales: Entregado pero sin pagos totales) ──
        var unassignedPaymentOrders = deliveredOrders
            .Where(o => !(o.Payments?.Any() ?? false) || o.Payments.Sum(p => p.Amount) < o.Total)
            .ToList();
        var unassignedAmount = unassignedPaymentOrders.Sum(o => o.Total - (o.Payments?.Sum(p => p.Amount) ?? 0));

        // ── 6. Métricas Operativas y Top ──
        var topProducts = orders
            .SelectMany(o => o.Items)
            .GroupBy(i => i.ProductName)
            .Select(g => new TopProductDto(g.Key, g.Sum(i => i.Quantity), g.Sum(i => i.LineTotal)))
            .OrderByDescending(p => p.Revenue)
            .Take(10)
            .ToList();

        var ordersByDay = orders
            .GroupBy(o => o.CreatedAt.ToString("yyyy-MM-dd"))
            .Select(g => new DailyCountDto(g.Key, g.Count(), g.Sum(o => o.Total)))
            .OrderBy(d => d.Date)
            .ToList();

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

        var supplierSummaries = await _db.Investments
            .Include(i => i.Supplier)
            .Where(i => i.Date >= startDate && i.Date < endDateExclusive)
            .GroupBy(i => i.Supplier.Name)
            .Select(g => new SupplierSummaryDto(
                g.Key,
                g.Sum(i => i.Amount * i.ExchangeRate),
                g.Count()
            ))
            .ToListAsync();

        // ── 7. Performance ──
        var routes = await _db.DeliveryRoutes
            .Where(r => r.CreatedAt >= startDate && r.CreatedAt < endDateExclusive)
            .ToListAsync();

        var totalDeliveries = await _db.Deliveries
            .Where(d => d.DeliveryRoute.CreatedAt >= startDate && d.DeliveryRoute.CreatedAt < endDateExclusive)
            .CountAsync();

        var deliveredCount = await _db.Deliveries
            .Where(d => d.DeliveryRoute.CreatedAt >= startDate && d.DeliveryRoute.CreatedAt < endDateExclusive
                     && d.Status == DeliveryStatus.Delivered)
            .CountAsync();

        var successRate = totalDeliveries > 0 ? Math.Round((decimal)deliveredCount / totalDeliveries * 100, 1) : 0m;

        var deliveriesWithTime = await _db.Deliveries
            .Include(d => d.DeliveryRoute)
            .Where(d => d.Status == DeliveryStatus.Delivered && d.DeliveredAt != null && d.DeliveryRoute.StartedAt != null 
                     && d.DeliveredAt >= startDate && d.DeliveredAt < endDateExclusive)
            .Select(d => new { d.DeliveredAt, d.DeliveryRoute.StartedAt })
            .ToListAsync();

        var avgDeliveryTime = deliveriesWithTime.Any() 
            ? deliveriesWithTime.Average(d => (d.DeliveredAt!.Value - d.StartedAt!.Value).TotalMinutes) 
            : 0;

        var completedRoutes = routes.Where(r => r.Status == RouteStatus.Completed && r.StartedAt != null && r.CompletedAt != null).ToList();
        var avgRouteTime = completedRoutes.Any()
            ? completedRoutes.Average(r => (r.CompletedAt!.Value - r.StartedAt!.Value).TotalMinutes)
            : 0;

        // ── 8. Comparativa con el periodo anterior ──
        var duration = endDateExclusive - startDate;
        var prevStartDate = startDate - duration;
        var prevEndDateExclusive = startDate;

        var prevPeriodRevenue = await _db.Deliveries
            .Where(d => d.Status == DeliveryStatus.Delivered && d.DeliveredAt >= prevStartDate && d.DeliveredAt < prevEndDateExclusive)
            .SumAsync(d => (decimal?)d.Order.Total) ?? 0m;

        var prevPeriodOrders = await _db.Orders
            .CountAsync(o => o.CreatedAt >= prevStartDate && o.CreatedAt < prevEndDateExclusive);

        return Ok(new ReportDto(
            TotalRevenue: revenue,
            TotalCollected: totalCollected,
            TotalInvestment: totalInvestment,
            TotalExpenses: driverExpenses,
            NetProfit: revenue - totalInvestment - driverExpenses,
            CashBalance: totalCollected - totalInvestment - driverExpenses, // Collected real
            TotalOrders: orders.Count(o => o.CreatedAt >= startDate && o.CreatedAt < endDateExclusive),
            PendingOrders: orders.Count(o => o.CreatedAt >= startDate && o.CreatedAt < endDateExclusive && o.Status == Models.OrderStatus.Pending),
            InRouteOrders: orders.Count(o => o.CreatedAt >= startDate && o.CreatedAt < endDateExclusive && o.Status == Models.OrderStatus.InRoute),
            DeliveredOrders: deliveredOrders.Count,
            NotDeliveredOrders: orders.Count(o => o.CreatedAt >= startDate && o.CreatedAt < endDateExclusive && o.Status == Models.OrderStatus.NotDelivered),
            CanceledOrders: orders.Count(o => o.CreatedAt >= startDate && o.CreatedAt < endDateExclusive && o.Status == Models.OrderStatus.Canceled),
            DeliveryOrders: orders.Count(o => o.CreatedAt >= startDate && o.CreatedAt < endDateExclusive && o.OrderType == OrderType.Delivery),
            PickUpOrders: orders.Count(o => o.CreatedAt >= startDate && o.CreatedAt < endDateExclusive && o.OrderType == OrderType.PickUp),
            AvgTicket: (decimal)(deliveredOrders.Count > 0 ? (double)Math.Round(deliveredOrders.Average(o => o.Total), 2) : 0),
            TopProducts: topProducts,
            OrdersByDay: ordersByDay,
            TotalRoutes: routes.Count,
            CompletedRoutes: completedRoutes.Count,
            SuccessRate: (decimal)(double)successRate,
            TotalDriverExpenses: driverExpenses,
            NewClients: newClients,
            FrequentClients: frequentClients,
            ActiveClients: clientsInPeriod.Count,
            TopClients: topClients,
            CashOrders: allPaymentsInRange.Count(p => p.Method == "Efectivo"),
            CashAmount: allPaymentsInRange.Where(p => p.Method == "Efectivo").Sum(p => p.Amount),
            TransferOrders: allPaymentsInRange.Count(p => p.Method == "Transferencia"),
            TransferAmount: allPaymentsInRange.Where(p => p.Method == "Transferencia").Sum(p => p.Amount),
            DepositOrders: allPaymentsInRange.Count(p => p.Method == "Deposito"),
            DepositAmount: allPaymentsInRange.Where(p => p.Method == "Deposito").Sum(p => p.Amount),
            UnassignedPaymentOrders: unassignedPaymentOrders.Count,
            UnassignedPaymentAmount: unassignedAmount,
            SupplierSummaries: supplierSummaries,
            AvgDeliveryTimeMinutes: Math.Round(avgDeliveryTime, 1),
            AvgRouteTimeMinutes: Math.Round(avgRouteTime, 1),
            PrevPeriodRevenue: prevPeriodRevenue,
            PrevPeriodOrders: prevPeriodOrders
        ));
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

        // 1. Process OrderType and ShippingCost changes FIRST
        if (!string.IsNullOrEmpty(req.OrderType) && Enum.TryParse<OrderType>(req.OrderType, true, out var newOrderType))
        {
            if (order.OrderType != newOrderType)
            {
                order.OrderType = newOrderType;
                if (newOrderType == OrderType.PickUp)
                {
                    order.ShippingCost = 0;
                }
                else if (newOrderType == OrderType.Delivery)
                {
                    // Ensure the fee is applied when switching to Delivery
                    order.ShippingCost = settings.DefaultShippingCost; 
                }
            }
        }

        // 2. Process Status changes
        Models.OrderStatus? parsedNewStatus = null;
        if (!string.IsNullOrEmpty(req.Status) && Enum.TryParse<Models.OrderStatus>(req.Status, true, out var parsedStatus))
        {
            parsedNewStatus = parsedStatus;
            int puntosCalculados = (int)(order.Total / 10m);

            // CASO A: Lo acaban de marcar como Entregado (+ PUNTOS)
            if (parsedNewStatus.Value == Models.OrderStatus.Delivered && order.Status != Models.OrderStatus.Delivered)
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
            else if (order.Status == Models.OrderStatus.Delivered && parsedNewStatus.Value != Models.OrderStatus.Delivered)
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
            order.Status = parsedNewStatus.Value;
        }

        order.PostponedAt = req.PostponedAt;
        order.PostponedNote = req.PostponedNote;
        order.Total = order.Subtotal + order.ShippingCost;

        await _db.SaveChangesAsync();

        // Enviar Push Notification basada en el nuevo estado
        if (parsedNewStatus.HasValue && parsedNewStatus.Value == EntregasApi.Models.OrderStatus.Shipped && order.Client != null)
        {
            await _pushService.SendNotificationToClientAsync(
                order.Client.Id, 
                "Tu pedido ha sido empacado 📦", 
                "Tu pedido está listo y empacado para entrega.", 
                $"/o/{order.AccessToken}");
        }
        else if (parsedNewStatus.HasValue && parsedNewStatus.Value == EntregasApi.Models.OrderStatus.Delivered && order.Client != null)
        {
            await _pushService.SendNotificationToClientAsync(
                order.Client.Id, 
                "Pedido Entregado 🌸", 
                "¡Gracias por tu compra en Regi Bazar!", 
                $"/o/{order.AccessToken}");
        }
        else if (parsedNewStatus.HasValue && parsedNewStatus.Value == EntregasApi.Models.OrderStatus.InRoute && order.Client != null)
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
            if (!string.IsNullOrEmpty(req.ClientType)) order.Client.Type = req.ClientType; // Added
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

        // SalesPeriod — permitir reasignar el corte desde edición
        if (req.SalesPeriodId.HasValue)
        {
            var periodExists = await _db.SalesPeriods.AnyAsync(p => p.Id == req.SalesPeriodId.Value);
            if (periodExists) order.SalesPeriodId = req.SalesPeriodId.Value;
        }
        else if (req.SalesPeriodId == null && order.SalesPeriodId.HasValue)
        {
            // Si mandan null explícitamente, desligar del corte
            order.SalesPeriodId = null;
        }

        order.PostponedAt = req.PostponedAt;
        order.PostponedNote = req.PostponedNote;
        order.Tags = req.Tags != null ? System.Text.Json.JsonSerializer.Serialize(req.Tags) : null;
        order.DeliveryTime = req.DeliveryTime;
        order.PickupDate = req.PickupDate;
        order.DeliveryInstructions = req.DeliveryInstructions;
        order.Total = order.Subtotal + order.ShippingCost;

        await _db.SaveChangesAsync();

        // Recargar nav prop para que MapToSummary incluya el nombre del corte
        await _db.Entry(order).Reference(o => o.SalesPeriod).LoadAsync();

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
            RegisteredBy = req.RegisteredBy ?? "Admin",  // ✅ Default seguro
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

    /// <summary>GET /api/orders/common-products - Sugerencias inteligentes</summary>
    [HttpGet("common-products")]
    [AllowAnonymous]
    public async Task<ActionResult<List<CommonProductDto>>> GetCommonProducts()
    {
        // Optimizamos: Obtenemos los nombres y conteos con una sola agregación agrupada.
        // El UnitPrice lo promediamos o tomamos el máximo para evitar el First() masivo.
        var products = await _db.OrderItems
            .GroupBy(i => i.ProductName)
            .Select(g => new {
                Name = g.Key,
                Count = g.Count(),
                // Simplificamos para que SQL sea ultra-rápido:
                Price = g.Max(i => i.UnitPrice) 
            })
            .OrderByDescending(p => p.Count)
            .Take(50)
            .ToListAsync();

        var result = products.Select(p => new CommonProductDto(p.Name, p.Count, p.Price)).ToList();

        return Ok(result);
    }

    // ---------------------------------------------------------
    // 📦 LOGÍSTICA DE BOLSAS Y ETIQUETAS (SISTEMA ANTI-PÉRDIDAS)
    // ---------------------------------------------------------

    /// <summary>GET /api/orders/{id}/packages - Lista las bolsas de un pedido</summary>
    [HttpGet("{id}/packages")]
    public async Task<ActionResult<List<OrderPackageDto>>> GetPackages(int id)
    {
        var packages = await _db.OrderPackages
            .Where(p => p.OrderId == id)
            .OrderBy(p => p.PackageNumber)
            .Select(p => new OrderPackageDto(
                p.Id,
                p.PackageNumber,
                p.QrCodeValue,
                p.Status.ToString(),
                p.CreatedAt,
                p.LoadedAt,
                p.DeliveredAt))
            .ToListAsync();

        return Ok(packages);
    }

    /// <summary>POST /api/orders/{id}/packages/generate - Crea bolsas nuevas e imprime QRs</summary>
    [HttpPost("{id}/packages/generate")]
    public async Task<IActionResult> GeneratePackages(int id, [FromBody] GeneratePackagesRequest req)
    {
        if (req.Count <= 0) return BadRequest("Debe generar al menos 1 bolsa.");

        var order = await _db.Orders
            .Include(o => o.Packages)
            .FirstOrDefaultAsync(o => o.Id == id);

        if (order == null) return NotFound("Pedido no encontrado.");

        // Averiguamos en qué número de bolsa nos quedamos (por si Miel agrega más días después)
        int startingNumber = order.Packages.Any() ? order.Packages.Max(p => p.PackageNumber) + 1 : 1;

        var newPackages = new List<OrderPackage>();

        for (int i = 0; i < req.Count; i++)
        {
            var packageId = Guid.NewGuid();
            // Creamos el QR infalible: RB-ORD[Id]-PKG[Primeros 8 chars del Guid]
            string qrText = $"RB-ORD{order.Id}-PKG{packageId.ToString().Substring(0, 8).ToUpper()}";

            var package = new OrderPackage
            {
                Id = packageId,
                OrderId = order.Id,
                PackageNumber = startingNumber + i,
                QrCodeValue = qrText,
                Status = PackageTrackingStatus.Packed,
                CreatedAt = DateTime.UtcNow
            };

            newPackages.Add(package);
            _db.OrderPackages.Add(package);
        }

        // Actualizamos las banderas rápidas de la orden principal
        order.TotalPackages = order.Packages.Count + newPackages.Count;
        order.IsFullyPacked = true;
        // Si agregamos nuevas bolsas, automáticamente ya no está "completamente cargada"
        order.IsFullyLoaded = false;

        await _db.SaveChangesAsync();

        var result = newPackages.Select(p => new OrderPackageDto(p.Id, p.PackageNumber, p.QrCodeValue, p.Status.ToString(), p.CreatedAt, p.LoadedAt, p.DeliveredAt));
        return Ok(result);
    }

    /// <summary>POST /api/orders/packages/scan - El motor del escáner nativo</summary>
    [HttpPost("packages/scan")]
    public async Task<IActionResult> ScanPackage([FromBody] ScanPackageRequest req)
    {
        // 1. Buscamos el QR en 1 milisegundo gracias al Índice Único
        var package = await _db.OrderPackages
            .Include(p => p.Order)
            .ThenInclude(o => o.Packages) // Traemos a sus hermanas para validar si ya están todas
            .FirstOrDefaultAsync(p => p.QrCodeValue == req.QrCodeValue);

        if (package == null) return NotFound(new { message = "Código QR no reconocido. Esta bolsa no es nuestra." });

        string successMessage = "";

        // 2. Máquina de Estados: ¿Qué está haciendo el chofer?
        if (req.Action.Equals("Load", StringComparison.OrdinalIgnoreCase))
        {
            if (package.Status >= PackageTrackingStatus.Loaded)
                return BadRequest(new { message = $"La bolsa {package.PackageNumber} ya estaba cargada en la camioneta." });

            package.Status = PackageTrackingStatus.Loaded;
            package.LoadedAt = DateTime.UtcNow;
            successMessage = $"Bolsa {package.PackageNumber} cargada con éxito.";
        }
        else if (req.Action.Equals("Deliver", StringComparison.OrdinalIgnoreCase))
        {
            if (package.Status == PackageTrackingStatus.Packed)
                return BadRequest(new { message = $"¡Peligro! Intentas entregar la bolsa {package.PackageNumber} pero el sistema no registra que la hayas subido a la camioneta." });

            if (package.Status == PackageTrackingStatus.Delivered)
                return BadRequest(new { message = $"La bolsa {package.PackageNumber} ya había sido entregada." });

            package.Status = PackageTrackingStatus.Delivered;
            package.DeliveredAt = DateTime.UtcNow;
            successMessage = $"Bolsa {package.PackageNumber} entregada a {package.Order.Client?.Name}.";
        }
        else
        {
            return BadRequest("Acción no válida. Usa 'Load' o 'Deliver'.");
        }

        // 3. Auditoría Maestra: ¿Ya terminamos con este pedido?
        var order = package.Order;

        // Revisa si TODAS las bolsas de este pedido ya están al menos 'Loaded'
        bool allLoaded = order.Packages.All(p => p.Status >= PackageTrackingStatus.Loaded);
        order.IsFullyLoaded = allLoaded;

        // Opcional: Si se escanean todas como Deliver, pasar la orden entera a Delivered automáticamente
        bool allDelivered = order.Packages.All(p => p.Status == PackageTrackingStatus.Delivered);
        if (allDelivered && order.Status != Models.OrderStatus.Delivered)
        {
            order.Status = Models.OrderStatus.Delivered;
            // Aquí podrías sumar puntos de lealtad llamando a tu lógica si lo deseas
        }

        await _db.SaveChangesAsync();

        return Ok(new
        {
            message = successMessage,
            orderId = order.Id,
            packageNumber = package.PackageNumber,
            totalPackages = order.TotalPackages,
            allPackagesLoaded = allLoaded,
            allPackagesDelivered = allDelivered
        });
    }

    [HttpDelete("wipe")]
    [Authorize]
    public async Task<IActionResult> WipeAllOrders()
    {
        await _db.OrderPayments.ExecuteDeleteAsync();
        await _db.OrderItems.ExecuteDeleteAsync();
        await _db.Deliveries.ExecuteDeleteAsync();
        await _db.Orders.ExecuteDeleteAsync();
        return NoContent();
    }
}