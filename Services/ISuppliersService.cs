using EntregasApi.DTOs;

namespace EntregasApi.Services
{
    public interface ISuppliersService
    {
        // Suppliers
        Task<List<SupplierDto>> GetAllSuppliersAsync();
        Task<SupplierDto?> GetSupplierByIdAsync(int id);
        Task<SupplierDto> CreateSupplierAsync(CreateSupplierRequest request);
        Task<SupplierDto?> UpdateSupplierAsync(int id, UpdateSupplierRequest request);
        Task<bool> DeleteSupplierAsync(int id);

        // Investments
        Task<List<InvestmentDto>> GetInvestmentsAsync(int supplierId);
        Task<InvestmentDto> CreateInvestmentAsync(int supplierId, CreateInvestmentRequest request);
        Task<bool> DeleteInvestmentAsync(int supplierId, int investmentId);
    }
}
