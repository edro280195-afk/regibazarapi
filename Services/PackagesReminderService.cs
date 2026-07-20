using EntregasApi.Data;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

/// <summary>
/// Recordatorios de "bolsas pendientes". Cada cierto tiempo revisa los pedidos
/// Pendientes/Confirmados que aún no tienen bolsas capturadas (PackagesConfirmed == false)
/// y le manda un push a la dueña: "¿Ya agregaste las bolsas de X?".
///
/// Reglas para no ser molesto:
///  - Solo dentro del horario diurno local (configurable).
///  - Un pedido no se vuelve a recordar hasta pasado el cooldown (PackagesReminderSentAt).
///  - Se agrupa en un solo push por corrida (no uno por pedido).
///  - Se ignoran pedidos recién creados (para dar chance de capturarlas en el momento).
///
/// Config opcional (appsettings, sección "PackagesReminder"), con defaults sensatos:
///   IntervalMinutes (180), MinOrderAgeMinutes (60), CooldownHours (8),
///   LocalUtcOffsetHours (-6, Nuevo Laredo), ActiveFromHour (9), ActiveToHour (21), Enabled (true)
/// </summary>
public class PackagesReminderService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<PackagesReminderService> _logger;

    public PackagesReminderService(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<PackagesReminderService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _config.GetValue("PackagesReminder:Enabled", true);
        if (!enabled)
        {
            _logger.LogInformation("PackagesReminderService deshabilitado por configuración.");
            return;
        }

        var interval = TimeSpan.FromMinutes(_config.GetValue("PackagesReminder:IntervalMinutes", 180));

        // Pequeña espera inicial para no correr en pleno arranque del contenedor.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en la corrida de recordatorios de bolsas.");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        // Horario local: solo molestamos de día.
        var offset = _config.GetValue("PackagesReminder:LocalUtcOffsetHours", -6.0);
        var fromHour = _config.GetValue("PackagesReminder:ActiveFromHour", 9);
        var toHour = _config.GetValue("PackagesReminder:ActiveToHour", 21);
        var localNow = DateTime.UtcNow.AddHours(offset);
        if (localNow.Hour < fromHour || localNow.Hour >= toHour)
            return;

        var minAge = TimeSpan.FromMinutes(_config.GetValue("PackagesReminder:MinOrderAgeMinutes", 60));
        var cooldown = TimeSpan.FromHours(_config.GetValue("PackagesReminder:CooldownHours", 8.0));
        var now = DateTime.UtcNow;
        var createdBefore = now - minAge;
        var remindableSince = now - cooldown;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var push = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();

        var pending = await db.Orders
            .Include(o => o.Client)
            .Where(o => !o.PackagesConfirmed
                        && o.DeliveryRouteId == null
                        && (o.Status == OrderStatus.Pending || o.Status == OrderStatus.Confirmed)
                        && o.CreatedAt <= createdBefore
                        && (o.PackagesReminderSentAt == null || o.PackagesReminderSentAt <= remindableSince))
            .OrderBy(o => o.CreatedAt)
            .Take(50)
            .ToListAsync(ct);

        if (pending.Count == 0)
            return;

        string title;
        string body;
        string url;

        if (pending.Count == 1)
        {
            var o = pending[0];
            var name = o.Client?.Name ?? "una clienta";
            title = "🛍️ ¿Cuántas bolsas?";
            body = $"El pedido de {name} sigue sin bolsas. Tócalo para agregarlas 💕";
            url = $"/admin/orders?focusOrder={o.Id}&bolsas=pendientes";
        }
        else
        {
            var names = pending.Take(3).Select(o => o.Client?.Name ?? "?").ToList();
            var extra = pending.Count - names.Count;
            var lista = string.Join(", ", names) + (extra > 0 ? $" y {extra} más" : "");
            title = $"🛍️ {pending.Count} pedidos sin bolsas";
            body = $"Faltan las bolsas de: {lista}. Tócalo para agregarlas 💕";
            url = "/admin/orders?bolsas=pendientes";
        }

        await push.SendNotificationToAdminsAsync(title, body, url, tag: "bolsas-pendientes");

        foreach (var o in pending)
            o.PackagesReminderSentAt = now;

        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Recordatorio de bolsas enviado para {Count} pedido(s).", pending.Count);
    }
}
