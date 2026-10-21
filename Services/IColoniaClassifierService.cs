using EntregasApi.DTOs;
using EntregasApi.Models;

namespace EntregasApi.Services;

public record ColoniaMatch(
    string Name,
    double Latitude,
    double Longitude,
    bool IsPickup
);

public interface IColoniaClassifierService
{
    ColoniaMatch ClassifyOrder(Order order);
    ColoniaMatch ClassifyAddress(string? address, double? latitude = null, double? longitude = null, OrderType? orderType = null);
    Task<ColoniaReportDto> GetColoniaReportAsync(DateTime startDate, DateTime endDateExclusive, int? salesPeriodId = null);
}
