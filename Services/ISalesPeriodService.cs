using EntregasApi.DTOs;

namespace EntregasApi.Services;

public interface ISalesPeriodService
{
    Task<List<SalesPeriodDto>> GetAllAsync();
    Task<SalesPeriodDto> CreateAsync(CreateSalesPeriodRequest request);
    Task<SalesPeriodDto?> ActivateAsync(int id);
    Task<PeriodReportDto?> GetPeriodReportAsync(int id);
    Task<int> SyncRelatedEntitiesAsync(int id, SyncSalesPeriodRequest request);
}
