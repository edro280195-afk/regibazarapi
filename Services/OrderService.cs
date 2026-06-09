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
            // Regla de negocio:
            //   • Entrega programada: próximo domingo (no se mueve).
            //   • Vigencia del enlace: 2 días después de la entrega (martes 23:59
            //     hora México cuando la entrega es domingo).
            var localDelivery = NextSunday(createdAt);
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

        // Vigencia: la entrega es el próximo domingo y el enlace expira 2 días
        // después (martes 23:59 hora México).
        var localDelivery = NextSunday(createdAt);
        var localExpiration = localDelivery.AddDays(2);

        return TimeZoneInfo.ConvertTimeToUtc(localExpiration, mexicoZone);
    }

    /// <summary>
    /// Devuelve la fecha (sin hora) del próximo domingo en hora local de México,
    /// a partir de la fecha/hora UTC dada. Si la fecha de creación YA es domingo,
    /// devuelve ese mismo día (la clienta ya puede confirmar/entregarse hoy).
    /// </summary>
    private static DateTime NextSunday(DateTime createdAtUtc)
    {
        var mexicoZone = BackendExtensions.GetMexicoZone();
        if (createdAtUtc.Kind == DateTimeKind.Unspecified)
        {
            createdAtUtc = DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc);
        }
        var localCreated = TimeZoneInfo.ConvertTimeFromUtc(createdAtUtc, mexicoZone).Date;

        // DayOfWeek.Sunday = 0. Si es domingo, devolvemos hoy mismo.
        if (localCreated.DayOfWeek == DayOfWeek.Sunday) return localCreated;

        int daysUntilSunday = (7 - (int)localCreated.DayOfWeek) % 7;
        if (daysUntilSunday == 0) daysUntilSunday = 7;
        return localCreated.AddDays(daysUntilSunday);
    }
}
