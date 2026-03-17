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
}
