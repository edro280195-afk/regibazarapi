using EntregasApi.DTOs;
using EntregasApi.Models;

namespace EntregasApi.Services;

public interface IRouteSettlementService
{
    Task<RouteSettlementDto> GetSettlementAsync(int routeId, string baseUrl);
    Task<RouteSettlementDto> SettleRouteAsync(int routeId, RouteSettlementRequest req, string baseUrl);
    Task<Dictionary<int, List<ActiveTandaInfoDto>>> GetActiveTandasForClientsAsync(IEnumerable<int> clientIds);
    Task<bool> RecordDriverTandaPaymentAsync(int routeId, DriverTandaPaymentRequest req);
}
