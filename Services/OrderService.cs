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
                order.ExpiresAt = CalculateExpiration(client.Type, order.CreatedAt);
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
