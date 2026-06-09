namespace EntregasApi.Services;

public interface IOrderService
{
    /// <summary>
    /// Recalculates and updates expiration dates for all Pending orders
    /// of the given client, based on their current Type (Nueva / Frecuente).
    /// </summary>
    Task SyncOrderExpirationsAsync(int clientId);

    /// <summary>
    /// Calculates the expiration date based on business rules.
    /// The link lives 2 days after the scheduled delivery (Tuesday 23:59 local
    /// when delivery is on Sunday).
    /// </summary>
    DateTime CalculateExpiration(string clientType, DateTime createdAt);

    /// <summary>
    /// Calculates both Expiration and Scheduled Delivery Date.
    /// If manualDate is provided: ExpiresAt = manualDate + 2 days.
    /// If not: ScheduledDeliveryDate = next Sunday, ExpiresAt = Sunday + 2 days.
    /// </summary>
    (DateTime ExpiresAt, DateTime ScheduledDeliveryDate) CalculateOrderDates(string clientType, DateTime createdAt, DateTime? manualDate = null);
}
