using EntregasApi.Data;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public class OrderService : IOrderService
{
    private readonly AppDbContext _db;

    public OrderService(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task SyncOrderExpirationsAsync(int clientId)
    {
        try
        {
            var client = await _db.Clients.FindAsync(clientId);
            if (client == null) return;

            var pendingOrders = await _db.Orders
                .Where(o => o.ClientId == clientId && o.Status == OrderStatus.Pending)
                .ToListAsync();

            if (!pendingOrders.Any()) return;

            foreach (var order in pendingOrders)
            {
                var dates = CalculateOrderDates(client.Type, order.CreatedAt, order.ScheduledDeliveryDate);
                order.ExpiresAt = dates.ExpiresAt;
                order.ScheduledDeliveryDate = dates.ScheduledDeliveryDate;
            }

            // No llamamos SaveChangesAsync aquí — el llamador decide cuándo guardar
            // para permitir agrupar con otras operaciones en la misma transacción.
        }
        catch (Exception ex)
        {
            // Log exception here in a real scenario
            Console.WriteLine($"Error syncing order expirations for client {clientId}: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public (DateTime ExpiresAt, DateTime ScheduledDeliveryDate) CalculateOrderDates(string clientType, DateTime createdAt, DateTime? manualDate = null)
    {
        var mexicoZone = BackendExtensions.GetMexicoZone();

        if (manualDate.HasValue)
        {
            // Si hay fecha manual (asumimos que viene como Date local sin hora)
            DateTime localDelivery;
            if (manualDate.Value.Kind == DateTimeKind.Utc)
            {
                localDelivery = TimeZoneInfo.ConvertTimeFromUtc(manualDate.Value, mexicoZone).Date;
            }
            else
            {
                localDelivery = manualDate.Value.Date;
            }

            // El vencimiento es 2 días después de la entrega (martes 23:59 si
            // la entrega es domingo, pero aplica a cualquier día manual).
            var localExpiration = localDelivery.AddDays(2);

            return (
                TimeZoneInfo.ConvertTimeToUtc(localExpiration, mexicoZone),
                TimeZoneInfo.ConvertTimeToUtc(localDelivery, mexicoZone)
            );
        }
        else
        {
            // Regla de negocio (según tipo de clienta):
            //   • Nueva:              entrega el PRÓXIMO domingo.
            //   • Frecuente / VIP:    entrega el SEGUNDO domingo (próximo + 7 días).
            //   • Vigencia del enlace: 2 días después de la entrega (martes 23:59
            //     hora México cuando la entrega es domingo).
            var localDelivery = ComputeLocalDeliveryDate(clientType, createdAt);
            var localExpiration = localDelivery.AddDays(2);

            return (
                TimeZoneInfo.ConvertTimeToUtc(localExpiration, mexicoZone),
                TimeZoneInfo.ConvertTimeToUtc(localDelivery, mexicoZone)
            );
        }
    }

    /// <inheritdoc />
    public DateTime CalculateExpiration(string clientType, DateTime createdAt)
    {
        var mexicoZone = BackendExtensions.GetMexicoZone();

        // Enforce UTC kind to prevent ArgumentException from TimeZoneInfo.ConvertTimeFromUtc
        if (createdAt.Kind == DateTimeKind.Unspecified)
        {
            createdAt = DateTime.SpecifyKind(createdAt, DateTimeKind.Utc);
        }

        // Vigencia: la entrega depende del tipo de clienta (Nueva = próximo domingo;
        // Frecuente/VIP = segundo domingo) y el enlace expira 2 días después.
        var localDelivery = ComputeLocalDeliveryDate(clientType, createdAt);
        var localExpiration = localDelivery.AddDays(2);

        return TimeZoneInfo.ConvertTimeToUtc(localExpiration, mexicoZone);
    }

    /// <summary>
    /// Fecha (sin hora, hora local de México) de la entrega programada según el tipo
    /// de clienta:
    ///   • Nueva:           el próximo domingo.
    ///   • Frecuente / VIP: el segundo domingo (próximo domingo + 7 días).
    /// </summary>
    private static DateTime ComputeLocalDeliveryDate(string? clientType, DateTime createdAtUtc)
    {
        var localDelivery = NextSunday(createdAtUtc);
        if (IsFrequentType(clientType)) localDelivery = localDelivery.AddDays(7);
        return localDelivery;
    }

    /// <summary>Frecuente y VIP comparten regla de entrega (segundo domingo).</summary>
    private static bool IsFrequentType(string? clientType)
    {
        if (string.IsNullOrWhiteSpace(clientType)) return false;
        return clientType.Trim().Equals("Frecuente", StringComparison.OrdinalIgnoreCase)
            || clientType.Trim().Equals("VIP", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Devuelve la fecha (sin hora) del PRÓXIMO domingo en hora local de México a
    /// partir de la fecha/hora UTC dada. Si la fecha de creación ya es domingo,
    /// cuenta como pasado y devuelve el domingo siguiente (la entrega nunca es "hoy").
    /// </summary>
    private static DateTime NextSunday(DateTime createdAtUtc)
    {
        var mexicoZone = BackendExtensions.GetMexicoZone();
        if (createdAtUtc.Kind == DateTimeKind.Unspecified)
        {
            createdAtUtc = DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc);
        }
        var localCreated = TimeZoneInfo.ConvertTimeFromUtc(createdAtUtc, mexicoZone).Date;

        // DayOfWeek.Sunday = 0. Si hoy es domingo, (7-0)%7 = 0 → se fuerza a 7 para
        // caer en el domingo siguiente.
        int daysUntilSunday = (7 - (int)localCreated.DayOfWeek) % 7;
        if (daysUntilSunday == 0) daysUntilSunday = 7;
        return localCreated.AddDays(daysUntilSunday);
    }
}
