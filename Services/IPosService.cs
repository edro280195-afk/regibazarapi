using EntregasApi.Models;

namespace EntregasApi.Services;

public interface IPosService
{
    Task<Order> ScanItemAsync(int orderId, string sku);
    Task<Order> RemoveItemAsync(int orderItemId);
    Task<CashRegisterSession> OpenSessionAsync(int userId, decimal initialCash);
    Task<CashRegisterSession> CloseSessionAsync(int sessionId, decimal actualCash);
    Task<CashRegisterSession?> GetActiveSessionAsync();
    Task<List<Order>> GetPendingOrdersAsync();
    Task<Order> CreatePosOrderAsync(string clientName);
    Task<Order> AddManualItemAsync(int orderId, string productName, decimal price, int quantity);
    Task<Order> RemoveItemByNameAsync(int orderId, string productName);
    Task<Order> ClearPosOrderAsync(int orderId);
    Task<Order> ApplyDiscountAsync(int orderId, decimal discountAmount);
    Task<OrderPayment> PayPosOrderAsync(int orderId, int sessionId, decimal amountReceived, string paymentMethod);
}
