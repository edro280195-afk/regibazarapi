using EntregasApi.Data;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

/// <summary>
/// Dispara las "campanitas" de pedidos (<see cref="OrderReminder"/>): cuando llega la hora
/// del aviso (RemindAt), manda un push a todos los dispositivos admin suscritos, con la
/// hora que pidió la clienta y la nota de la dueña. Tocar el aviso abre el pedido.
///
/// Diseño:
///  - Revisa cada minuto. Cualquier aviso vencido y sin enviar se manda, así que si el
///    servidor estuvo caído o se redesplegó, el aviso sale al volver (entrega "al menos una vez").
///  - A diferencia de los recordatorios genéricos, NO respeta horario diurno: la dueña
///    eligió esa hora a propósito.
///  - Solo se da por enviado si el push llegó al menos a un dispositivo. Si no hay ninguno
///    suscrito (la dueña aún no activa notificaciones) o el envío falla, se reintenta en cada
///    corrida: sale en cuanto haya un dispositivo que lo reciba.
///  - Un aviso que lleva demasiado tiempo vencido (MaxLateHours) se descarta sin push,
///    porque a esas alturas solo sería ruido.
///  - Pedidos entregados o cancelados no avisan.
///  - Un push por campanita (tag propio) para que no se reemplacen entre sí.
///
/// Config opcional (sección "OrderReminders"):
///   Enabled (true), IntervalSeconds (60), MaxLateHours (12)
/// </summary>
public class OrderRemindersService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<OrderRemindersService> _logger;

    // Campanitas cuyo fallo de entrega ya se registró, para no llenar el log con un aviso por minuto.
    private readonly HashSet<int> _failureLogged = new();

    public OrderRemindersService(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<OrderRemindersService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue("OrderReminders:Enabled", true))
        {
            _logger.LogInformation("OrderRemindersService deshabilitado por configuración.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(15, _config.GetValue("OrderReminders:IntervalSeconds", 60)));

        // Espera corta al arrancar para no competir con migraciones y warmup.
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error en la corrida de campanitas de pedidos.");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var maxLate = TimeSpan.FromHours(_config.GetValue("OrderReminders:MaxLateHours", 12.0));
        var zone = ResolveBusinessZone();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var push = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();

        var due = await db.OrderReminders
            .Include(r => r.Order).ThenInclude(o => o.Client)
            .Where(r => r.SentAt == null
                        && r.RemindAt <= now
                        && r.Order.Status != OrderStatus.Delivered
                        && r.Order.Status != OrderStatus.Canceled)
            .OrderBy(r => r.RemindAt)
            .Take(50)
            .ToListAsync(ct);

        foreach (var reminder in due)
        {
            if (now - reminder.RemindAt > maxLate)
            {
                _logger.LogWarning(
                    "Campanita {ReminderId} (pedido {OrderId}) llevaba {Late} vencida; se descarta sin push.",
                    reminder.Id, reminder.OrderId, now - reminder.RemindAt);
            }
            else
            {
                var (title, body) = OrderReminderPolicy.BuildMessage(
                    reminder.Order.Client?.Name ?? string.Empty,
                    reminder.TargetAt, reminder.Note, now, zone);

                var delivered = await push.TrySendNotificationToAdminsAsync(
                    title, body,
                    url: $"/admin/orders?focusOrder={reminder.OrderId}",
                    tag: $"order-reminder-{reminder.Id}");

                if (delivered == 0)
                {
                    // Nadie lo recibió: NO se marca como enviada, se reintenta en la siguiente corrida.
                    if (_failureLogged.Add(reminder.Id))
                        _logger.LogWarning(
                            "Campanita {ReminderId} (pedido {OrderId}): el push no llegó a ningún dispositivo admin " +
                            "(¿sin suscripciones o VAPID mal configurado?). Se reintenta hasta {MaxLate}.",
                            reminder.Id, reminder.OrderId, maxLate);
                    continue;
                }

                _failureLogged.Remove(reminder.Id);
                _logger.LogInformation(
                    "Campanita {ReminderId} enviada a {Devices} dispositivo(s) (pedido {OrderId}).",
                    reminder.Id, delivered, reminder.OrderId);
            }

            // Se marca una por una: si algo falla a medias, las ya avisadas no se repiten.
            _failureLogged.Remove(reminder.Id);
            reminder.SentAt = now;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Zona del negocio (Nuevo Laredo). Si el contenedor no trae tzdata, cae a UTC-6 fijo.</summary>
    private static TimeZoneInfo ResolveBusinessZone()
    {
        try { return BackendExtensions.GetMexicoZone(); }
        catch (Exception)
        {
            return TimeZoneInfo.CreateCustomTimeZone("MX-fallback", TimeSpan.FromHours(-6), "MX", "MX");
        }
    }
}
