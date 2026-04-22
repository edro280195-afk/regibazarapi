namespace EntregasApi.Services;

public interface IOrderService
{
    /// <summary>
    /// Recalculates and updates expiration dates for all Pending orders
    /// of the given client, based on their current Type (Nueva / Frecuente).
    /// </summary>
    Task SyncOrderExpirationsAsync(int clientId);

    /// <summary>
    /// Calculates the expiration date based on business rules:
    /// - Nueva: Next Monday from createdAt.
    /// - Frecuente: Monday after next from createdAt.
    /// </summary>
    DateTime CalculateExpiration(string clientType, DateTime createdAt);

    /// <summary>
    /// Calculates both Expiration and Scheduled Delivery Date.
    /// If manualDate is provided: ExpiresAt = manualDate + 2 days.
    /// If not: Uses standard logic and sets ScheduledDeliveryDate to Sunday before expiration.
    /// </summary>
    (DateTime ExpiresAt, DateTime ScheduledDeliveryDate) CalculateOrderDates(string clientType, DateTime createdAt, DateTime? manualDate = null);
}
