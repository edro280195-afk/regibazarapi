using System.Globalization;
using EntregasApi.Data;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

/// <summary>
/// Notificaciones "inteligentes" para la dueña: avisos periódicos suaves y un pulso
/// del negocio de vez en cuando, para que esté enterada y nada se le pase, sin
/// saturarla. Cada chequeo suena a lo mucho una vez al día (o a la semana el pulso),
/// solo en horario diurno y solo si de verdad hay algo que decir.
///
/// Chequeos:
///   • ⌛ Pedidos por vencer  — pendientes cuya vigencia vence pronto sin confirmar.
///   • 💰 Saldos sin cobrar   — entregados que siguen debiendo desde hace días.
///   • 💗 Pulso del negocio   — 1 vez por semana, un insight rotativo (ventas vs.
///     semana pasada, pendientes acumulados, mejor clienta…).
///
/// Config opcional (sección "AdminNotifications"), con defaults sensatos:
///   IntervalMinutes (30), LocalUtcOffsetHours (-6), MorningHour (9),
///   WeeklyInsightDay (1 = lunes), ExpiringSoonHours (36), UnpaidMinDays (3),
///   Enabled (true).
/// </summary>
public class AdminNotificationsService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<AdminNotificationsService> _logger;

    // Guardas en memoria para no repetir un aviso el mismo día / semana.
    private readonly Dictionary<string, string> _lastFired = new();

    public AdminNotificationsService(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<AdminNotificationsService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue("AdminNotifications:Enabled", true))
        {
            _logger.LogInformation("AdminNotificationsService deshabilitado por configuración.");
            return;
        }

        var interval = TimeSpan.FromMinutes(_config.GetValue("AdminNotifications:IntervalMinutes", 30));

        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunChecksAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Error en AdminNotificationsService."); }

            try { await Task.Delay(interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task RunChecksAsync(CancellationToken ct)
    {
        var offset = _config.GetValue("AdminNotifications:LocalUtcOffsetHours", -6.0);
        var morningHour = _config.GetValue("AdminNotifications:MorningHour", 9);
        var weeklyDay = _config.GetValue("AdminNotifications:WeeklyInsightDay", 1); // 1 = lunes
        var localNow = DateTime.UtcNow.AddHours(offset);

        // Todo suena en la ventana de la mañana (una hora), una sola vez gracias a la guarda.
        if (localNow.Hour != morningHour) return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var push = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();

        var today = localNow.ToString("yyyy-MM-dd");

        if (ShouldFire("por-vencer", today))
            await CheckExpiringSoonAsync(db, push, ct);

        if (ShouldFire("saldos", today))
            await CheckUnpaidAsync(db, push, ct);

        // Pulso del negocio: solo el día configurado, una vez por semana.
        if ((int)localNow.DayOfWeek == weeklyDay)
        {
            var weekToken = $"{localNow.Year}-W{ISOWeek.GetWeekOfYear(localNow)}";
            if (ShouldFire("pulso", weekToken))
                await SendBusinessPulseAsync(db, push, localNow, ct);
        }
    }

    /// <summary>Devuelve true (y marca como disparado) si este aviso aún no ha sonado en el token dado.</summary>
    private bool ShouldFire(string key, string token)
    {
        if (_lastFired.TryGetValue(key, out var last) && last == token) return false;
        _lastFired[key] = token;
        return true;
    }

    // ── ⌛ Pedidos por vencer ──
    private async Task CheckExpiringSoonAsync(AppDbContext db, IPushNotificationService push, CancellationToken ct)
    {
        var hours = _config.GetValue("AdminNotifications:ExpiringSoonHours", 36);
        var now = DateTime.UtcNow;
        var limit = now.AddHours(hours);

        var soon = await db.Orders
            .Include(o => o.Client)
            .Where(o => o.Status == OrderStatus.Pending
                        && o.DeliveryRouteId == null
                        && o.ExpiresAt > now
                        && o.ExpiresAt <= limit)
            .OrderBy(o => o.ExpiresAt)
            .Take(50)
            .ToListAsync(ct);

        if (soon.Count == 0) return;

        var names = soon.Take(3).Select(o => o.Client?.Name ?? "?").ToList();
        var extra = soon.Count - names.Count;
        var lista = string.Join(", ", names) + (extra > 0 ? $" y {extra} más" : "");

        var body = soon.Count == 1
            ? $"El pedido de {names[0]} está por vencer y aún no se confirma. ¿Le hablas? 💕"
            : $"{soon.Count} pedidos están por vencer sin confirmar: {lista}.";

        await push.SendNotificationToAdminsAsync("⌛ Pedidos por vencer", body, "/admin/orders", "pedidos-por-vencer");
        _logger.LogInformation("Aviso 'por vencer' enviado ({Count}).", soon.Count);
    }

    // ── 💰 Saldos sin cobrar ──
    private async Task CheckUnpaidAsync(AppDbContext db, IPushNotificationService push, CancellationToken ct)
    {
        var minDays = _config.GetValue("AdminNotifications:UnpaidMinDays", 3);
        var cutoff = DateTime.UtcNow.AddDays(-minDays);

        // BalanceDue es calculado ([NotMapped]) → traemos con pagos y filtramos en memoria.
        var delivered = await db.Orders
            .Include(o => o.Client)
            .Include(o => o.Payments)
            .Where(o => o.Status == OrderStatus.Delivered && o.Total > 0 && o.CreatedAt <= cutoff)
            .ToListAsync(ct);

        var conSaldo = delivered.Where(o => o.BalanceDue > 0).ToList();
        if (conSaldo.Count == 0) return;

        var totalAdeudado = conSaldo.Sum(o => o.BalanceDue);
        var top = conSaldo.OrderByDescending(o => o.BalanceDue).First();

        var body = conSaldo.Count == 1
            ? $"{top.Client?.Name ?? "Una clienta"} debe ${top.BalanceDue:N0} de un pedido ya entregado."
            : $"{conSaldo.Count} pedidos entregados siguen sin cobrarse (${totalAdeudado:N0} en total). El mayor: {top.Client?.Name ?? "?"} (${top.BalanceDue:N0}).";

        await push.SendNotificationToAdminsAsync("💰 Saldos sin cobrar", body, "/admin/orders", "saldos-sin-cobrar");
        _logger.LogInformation("Aviso 'saldos' enviado ({Count}).", conSaldo.Count);
    }

    // ── 💗 Pulso del negocio (insight rotativo) ──
    private async Task SendBusinessPulseAsync(AppDbContext db, IPushNotificationService push, DateTime localNow, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var weekAgo = now.AddDays(-7);
        var twoWeeksAgo = now.AddDays(-14);

        // Entregas (con su Order) de las últimas dos semanas, para varios insights.
        var recent = await db.Deliveries
            .Include(d => d.Order).ThenInclude(o => o!.Client)
            .Where(d => d.Status == DeliveryStatus.Delivered
                        && d.DeliveredAt != null
                        && d.DeliveredAt >= twoWeeksAgo
                        && d.Order != null)
            .ToListAsync(ct);

        var thisWeek = recent.Where(d => d.DeliveredAt >= weekAgo).ToList();
        var prevWeek = recent.Where(d => d.DeliveredAt < weekAgo).ToList();

        decimal ventasEsta = thisWeek.Sum(d => d.Order!.Total);
        decimal ventasPrev = prevWeek.Sum(d => d.Order!.Total);

        // Rotamos el insight por número de semana para que se sienta variado.
        int variante = ISOWeek.GetWeekOfYear(localNow) % 3;

        string? title = null;
        string? body = null;

        // Insight 0: ventas de la semana vs. la anterior
        if (variante == 0 && (thisWeek.Count > 0 || prevWeek.Count > 0))
        {
            title = "💗 Tu semana en Regi Bazar";
            if (ventasPrev > 0)
            {
                var delta = (ventasEsta - ventasPrev) / ventasPrev * 100m;
                var flecha = delta >= 0 ? "📈" : "📉";
                var comp = delta >= 0 ? $"{delta:N0}% más" : $"{Math.Abs(delta):N0}% menos";
                body = $"Vendiste ${ventasEsta:N0} en {thisWeek.Count} entregas — {comp} que la semana pasada {flecha}";
            }
            else
            {
                body = $"Vendiste ${ventasEsta:N0} en {thisWeek.Count} entregas esta semana ✨";
            }
        }

        // Insight 1: mejor clienta de la semana
        if (body == null && variante == 1 && thisWeek.Count > 0)
        {
            var mejor = thisWeek
                .GroupBy(d => d.Order!.Client?.Name ?? "?")
                .Select(g => new { Name = g.Key, Total = g.Sum(x => x.Order!.Total) })
                .OrderByDescending(x => x.Total)
                .First();
            title = "👑 Tu mejor clienta de la semana";
            body = $"{mejor.Name} se llevó ${mejor.Total:N0} esta semana. ¡Consiéntela! 💕";
        }

        // Insight 2 (y fallback): pendientes acumulados
        if (body == null)
        {
            var pendientes = await db.Orders
                .Where(o => o.Status == OrderStatus.Pending)
                .OrderBy(o => o.CreatedAt)
                .ToListAsync(ct);

            if (pendientes.Count > 0)
            {
                var masViejo = (int)(now - pendientes[0].CreatedAt).TotalDays;
                title = "📋 Tus pedidos pendientes";
                body = pendientes.Count == 1
                    ? $"Tienes 1 pedido pendiente, de hace {masViejo} día(s). ¿Lo cierras? 💕"
                    : $"Tienes {pendientes.Count} pedidos pendientes; el más viejo de hace {masViejo} día(s).";
            }
            else if (title == null)
            {
                // Nada pendiente y sin datos de ventas → mensajito positivo, sin ruido de números.
                title = "🌸 Todo al día";
                body = "No tienes pedidos pendientes. ¡Vas increíble! 💗";
            }
        }

        if (title != null && body != null)
        {
            await push.SendNotificationToAdminsAsync(title, body, "/admin", "pulso-negocio");
            _logger.LogInformation("Pulso del negocio enviado (variante {V}).", variante);
        }
    }
}
