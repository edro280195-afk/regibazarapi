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

            // El vencimiento es 2 días después de la entrega
            var localExpiration = localDelivery.AddDays(2);

            return (
                TimeZoneInfo.ConvertTimeToUtc(localExpiration, mexicoZone),
                TimeZoneInfo.ConvertTimeToUtc(localDelivery, mexicoZone)
            );
        }
        else
        {
            // Lógica estándar
            var expiresAt = CalculateExpiration(clientType, createdAt);
            
            // Si el vencimiento es un Lunes a las 00:00:00 (vía CalculateExpiration), 
            // la entrega programada es el Domingo (1 día antes)
            var localExpiresAt = TimeZoneInfo.ConvertTimeFromUtc(expiresAt, mexicoZone);
            var localDelivery = localExpiresAt.AddDays(-1).Date;

            return (
                expiresAt,
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

        // Convertimos la fecha base a hora local
        var localCreated = TimeZoneInfo.ConvertTimeFromUtc(createdAt, mexicoZone);

        // Calculamos cuántos días faltan para el próximo Lunes
        int daysUntilMonday = (8 - (int)localCreated.DayOfWeek) % 7;
        if (daysUntilMonday == 0) daysUntilMonday = 7;

        // Fecha base: El próximo Lunes a las 00:00:00 local
        DateTime localExpiration = localCreated.Date.AddDays(daysUntilMonday);

        // Regla de negocio: Si es Frecuente, +7 días extra
        if (clientType == "Frecuente")
        {
            localExpiration = localExpiration.AddDays(7);
        }

        // Devolvemos en UTC
        return TimeZoneInfo.ConvertTimeToUtc(localExpiration, mexicoZone);
    }
}
