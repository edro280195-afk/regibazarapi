using EntregasApi.DTOs;

namespace EntregasApi.Services
{
    public interface IExpenseService
    {
        Task<DriverExpenseDto> CreateExpenseAsync(string driverToken, CreateDriverExpenseRequest request, IFormFile? photo);
        Task<DriverExpenseDto> CreateAdminExpenseAsync(CreateAdminExpenseRequest request);
        Task<DriverExpenseDto> UpdateExpenseAsync(int id, UpdateAdminExpenseRequest request);
        Task<bool> DeleteExpenseAsync(int id);
        Task<List<DriverExpenseDto>> GetExpensesByPeriodAsync(string? period);
        Task<FinancialReportDto> GetFinancialReportAsync(DateTime startDate, DateTime endDate);
    }
}
