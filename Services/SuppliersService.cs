using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services
{
    public class SuppliersService : ISuppliersService
    {
        private readonly AppDbContext _db;

        public SuppliersService(AppDbContext db)
        {
            _db = db;
        }

        // ═══════════════════════════════════════════
        //  SUPPLIERS
        // ═══════════════════════════════════════════

        public async Task<List<SupplierDto>> GetAllSuppliersAsync()
        {
            return await _db.Suppliers
                .OrderBy(s => s.Name)
                .Select(s => MapToDto(s))
                .ToListAsync();
        }

        public async Task<SupplierDto?> GetSupplierByIdAsync(int id)
        {
            var supplier = await _db.Suppliers.FindAsync(id);
            return supplier == null ? null : MapToDto(supplier);
        }

        public async Task<SupplierDto> CreateSupplierAsync(CreateSupplierRequest request)
        {
            var supplier = new Supplier
            {
                Name = request.Name.Trim(),
                ContactName = request.ContactName?.Trim(),
                Phone = request.Phone?.Trim(),
                Notes = request.Notes?.Trim(),
                CreatedAt = DateTime.UtcNow
            };

            _db.Suppliers.Add(supplier);
            await _db.SaveChangesAsync();

            return MapToDto(supplier);
        }

        public async Task<SupplierDto?> UpdateSupplierAsync(int id, UpdateSupplierRequest request)
        {
            var supplier = await _db.Suppliers.FindAsync(id);
            if (supplier == null) return null;

            supplier.Name = request.Name.Trim();
            supplier.ContactName = request.ContactName?.Trim();
            supplier.Phone = request.Phone?.Trim();
            supplier.Notes = request.Notes?.Trim();

            await _db.SaveChangesAsync();

            return MapToDto(supplier);
        }

        public async Task<bool> DeleteSupplierAsync(int id)
        {
            var supplier = await _db.Suppliers
                .Include(s => s.Investments)
                .FirstOrDefaultAsync(s => s.Id == id);

            if (supplier == null) return false;

            // Eliminar inversiones asociadas (cascade manual por si no está configurado en DB)
            _db.Investments.RemoveRange(supplier.Investments);
            _db.Suppliers.Remove(supplier);
            await _db.SaveChangesAsync();

            return true;
        }

        // ═══════════════════════════════════════════
        //  INVESTMENTS
        // ═══════════════════════════════════════════

        public async Task<List<InvestmentDto>> GetInvestmentsAsync(int supplierId)
        {
            return await _db.Investments
                .Where(i => i.SupplierId == supplierId)
                .OrderByDescending(i => i.Date)
                .ThenByDescending(i => i.CreatedAt)
                .Select(i => MapToInvDto(i))
                .ToListAsync();
        }

        public async Task<InvestmentDto> CreateInvestmentAsync(int supplierId, CreateInvestmentRequest request)
        {
            // Validar que el proveedor exista
            var supplierExists = await _db.Suppliers.AnyAsync(s => s.Id == supplierId);
            if (!supplierExists)
                throw new InvalidOperationException($"Proveedor con Id {supplierId} no encontrado.");

            var investment = new Investment
            {
                SupplierId = supplierId,
                Amount = request.Amount,
                Date = request.Date,
                Notes = request.Notes?.Trim(),
                CreatedAt = DateTime.UtcNow
            };

            _db.Investments.Add(investment);
            await _db.SaveChangesAsync();

            return MapToInvDto(investment);
        }

        public async Task<bool> DeleteInvestmentAsync(int supplierId, int investmentId)
        {
            var investment = await _db.Investments
                .FirstOrDefaultAsync(i => i.Id == investmentId && i.SupplierId == supplierId);

            if (investment == null) return false;

            _db.Investments.Remove(investment);
            await _db.SaveChangesAsync();

            return true;
        }

        // ═══════════════════════════════════════════
        //  MAPPERS
        // ═══════════════════════════════════════════

        private static SupplierDto MapToDto(Supplier s) => new(
            s.Id,
            s.Name,
            s.ContactName,
            s.Phone,
            s.Notes,
            s.CreatedAt
        );

        private static InvestmentDto MapToInvDto(Investment i) => new(
            i.Id,
            i.SupplierId,
            i.Amount,
            i.Date,
            i.Notes,
            i.CreatedAt
        );
    }
}
