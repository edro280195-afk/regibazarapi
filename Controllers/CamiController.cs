using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CamiController : ControllerBase
{
    private readonly ICamiService _cami;
    private readonly IGoogleTtsService _tts;
    private readonly IGeminiService _gemini;
    private readonly AppDbContext _db;
    private readonly ILogger<CamiController> _logger;

    public CamiController(ICamiService cami, IGoogleTtsService tts, IGeminiService gemini, AppDbContext db, ILogger<CamiController> logger)
    {
        _cami = cami;
        _tts = tts;
        _gemini = gemini;
        _db = db;
        _logger = logger;
    }

    [HttpPost("chat")]
    public async Task<ActionResult<CamiChatResponse>> Chat([FromBody] CamiChatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.NewMessage))
            return BadRequest("El mensaje no puede estar vacío.");

        try
        {
            var text = await _cami.ChatAsync(request);
            string? audioBase64 = null;
            try { audioBase64 = await _tts.SynthesizeAsync(text); }
            catch (Exception ttsEx) { _logger.LogWarning(ttsEx, "TTS falló, enviando solo texto."); }
            return Ok(new CamiChatResponse(text, audioBase64));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en POST /api/cami/chat");
            return StatusCode(500, new CamiChatResponse("Lo siento, tuve un problema técnico. ¿Lo intentamos de nuevo?"));
        }
    }

    /// <summary>Dashboard AI insight — recibe los datos del dashboard y devuelve 2 oraciones.</summary>
    [HttpPost("dashboard-insight")]
    public async Task<ActionResult<CamiChatResponse>> DashboardInsight([FromBody] DashboardInsightRequest data)
    {
        try
        {
            var insight = await _gemini.GetDashboardInsightAsync(data);
            return Ok(new CamiChatResponse(insight));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en /cami/dashboard-insight");
            // DIAGNÓSTICO TEMPORAL: Devolvemos el error real para identificar la falla
            return Ok(new CamiChatResponse($"Error Dashboard: {ex.Message} (Check model/key)"));
        }
    }

    /// <summary>Client profile AI insight — genera narrativa de comportamiento de compra.</summary>
    [HttpGet("client-insight/{clientId}")]
    public async Task<ActionResult<CamiChatResponse>> ClientInsight(int clientId)
    {
        try
        {
            var client = await _db.Clients
                .Include(c => c.Orders).ThenInclude(o => o.Items)
                .Include(c => c.Orders).ThenInclude(o => o.Payments) // ✅ Incluimos pagos para saldo exacto
                .FirstOrDefaultAsync(c => c.Id == clientId);

            if (client == null) return NotFound("Cliente no encontrado.");

            var orders = client.Orders
                .Where(o => o.Status != OrderStatus.Canceled)
                .OrderByDescending(o => o.CreatedAt)
                .Take(20)
                .ToList();

            var topProducts = orders
                .SelectMany(o => o.Items)
                .GroupBy(i => i.ProductName)
                .OrderByDescending(g => g.Sum(i => i.Quantity))
                .Take(3)
                .Select(g => g.Key)
                .ToList();

            var avgDaysBetweenOrders = 0.0;
            if (orders.Count > 1)
            {
                var dates = orders.Select(o => o.CreatedAt).OrderBy(d => d).ToList();
                avgDaysBetweenOrders = Enumerable.Range(0, dates.Count - 1)
                    .Select(i => (dates[i + 1] - dates[i]).TotalDays)
                    .Average();
            }

            var pendingBalance = orders.Where(o => o.Status is OrderStatus.Pending or OrderStatus.Confirmed or OrderStatus.InRoute or OrderStatus.Delivered).Sum(o => o.BalanceDue);
            var lastOrderDays = orders.Any() ? (DateTime.UtcNow - orders.First().CreatedAt).TotalDays : 999;

            var data = new
            {
                nombre = client.Name,
                tipo = client.Type,
                nivel = client.Tag.ToString(),
                totalPedidos = orders.Count,
                totalGastado = orders.Sum(o => o.Total),
                saldoPendiente = pendingBalance,
                productosFavoritos = topProducts,
                diasPromedioEntrePedidos = Math.Round(avgDaysBetweenOrders, 1),
                diasDesdeUltimoPedido = Math.Round(lastOrderDays, 0),
                puntos = client.CurrentPoints
            };

            var insight = await _gemini.GetClientInsightAsync(data);
            return Ok(new CamiChatResponse(insight));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en /cami/client-insight/{ClientId}", clientId);
            // DIAGNÓSTICO TEMPORAL: Devolvemos el error real para identificar la falla
            return Ok(new CamiChatResponse($"Error Insight Cli: {ex.Message}. SDK/Auth issue?"));
        }
    }

    /// <summary>Route briefing — genera briefing de voz para el repartidor con TTS.</summary>
    [HttpGet("route-briefing/{routeId}")]
    public async Task<ActionResult<RouteBriefingResponse>> RouteBriefing(int routeId)
    {
        try
        {
            var route = await _db.DeliveryRoutes
                .Include(r => r.Deliveries).ThenInclude(d => d.Order).ThenInclude(o => o.Client)
                .Include(r => r.Deliveries).ThenInclude(d => d.Order).ThenInclude(o => o.Payments)
                .FirstOrDefaultAsync(r => r.Id == routeId);

            if (route == null) return NotFound("Ruta no encontrada.");

            var deliveries = route.Deliveries
                .OrderBy(d => d.SortOrder)
                .Select((d, i) => new
                {
                    numero = i + 1,
                    cliente = d.Order.Client.Name,
                    direccion = d.Order.Client.Address ?? "sin dirección registrada",
                    saldo = d.Order.BalanceDue,
                    total = d.Order.Total,
                    instrucciones = d.Order.Client.DeliveryInstructions
                })
                .ToList();

            var totalPorCobrar = deliveries.Sum(d => d.saldo);

            var data = new
            {
                rutaId = routeId,
                totalParadas = deliveries.Count,
                totalPorCobrar,
                paradas = deliveries
            };

            var text = await _gemini.GetRouteBriefingAsync(data);

            string? audio = null;
            try { audio = await _tts.SynthesizeAsync(text); }
            catch { /* TTS opcional */ }

            return Ok(new RouteBriefingResponse(text, audio));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en /cami/route-briefing/{RouteId}", routeId);
            return Ok(new RouteBriefingResponse("No pude generar el briefing en este momento."));
        }
    }

    /// <summary>Alertas proactivas del negocio.</summary>
    [HttpGet("alerts")]
    public async Task<ActionResult<List<CamiAlert>>> GetAlerts()
    {
        try
        {
            var alerts = new List<CamiAlert>();
            var now = DateTime.UtcNow;

            // 1. Pedidos entregados sin cobrar hace más de 5 días
            var sinCobrar = await _db.Orders
                .Include(o => o.Client)
                .Where(o => o.Status == OrderStatus.Delivered && o.BalanceDue > 0
                       && o.CreatedAt < now.AddDays(-5))
                .CountAsync();
            if (sinCobrar > 0)
                alerts.Add(new CamiAlert("cobranza", $"{sinCobrar} pedido{(sinCobrar > 1 ? "s" : "")} entregado{(sinCobrar > 1 ? "s" : "")} sin cobrar desde hace más de 5 días", "💸"));

            // 2. Pedidos pendientes sin movimiento hace más de 3 días
            var pedidosEstancados = await _db.Orders
                .Where(o => o.Status == OrderStatus.Pending && o.CreatedAt < now.AddDays(-3))
                .CountAsync();
            if (pedidosEstancados > 0)
                alerts.Add(new CamiAlert("atencion", $"{pedidosEstancados} pedido{(pedidosEstancados > 1 ? "s" : "")} pendiente{(pedidosEstancados > 1 ? "s" : "")} sin actualizar en más de 3 días", "⏳", pedidosEstancados));

            // 3. Rutas activas hace más de 8 horas
            var rutasViejas = await _db.DeliveryRoutes
                .Where(r => (r.Status == RouteStatus.Active || r.Status == RouteStatus.Pending)
                       && r.CreatedAt < now.AddHours(-8))
                .CountAsync();
            if (rutasViejas > 0)
                alerts.Add(new CamiAlert("rutas", $"{rutasViejas} ruta{(rutasViejas > 1 ? "s" : "")} lleva{(rutasViejas > 1 ? "n" : "")} más de 8 horas activa{(rutasViejas > 1 ? "s" : "")}", "🚗"));

            // 4. Pedidos pospuestos con fecha ya vencida
            var pospuestosVencidos = await _db.Orders
                .Where(o => o.Status == OrderStatus.Postponed && o.PostponedAt.HasValue && o.PostponedAt.Value < now)
                .CountAsync();
            if (pospuestosVencidos > 0)
                alerts.Add(new CamiAlert("pospuestos", $"{pospuestosVencidos} pedido{(pospuestosVencidos > 1 ? "s" : "")} pospuesto{(pospuestosVencidos > 1 ? "s" : "")} con fecha vencida", "📅"));

            return Ok(alerts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en /cami/alerts");
            return Ok(new List<CamiAlert>());
        }
    }
}
