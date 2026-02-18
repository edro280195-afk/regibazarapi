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
        //  SUPPLIERS (PROVEEDORES)
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

            // Eliminar inversiones asociadas
            _db.Investments.RemoveRange(supplier.Investments);
            _db.Suppliers.Remove(supplier);
            await _db.SaveChangesAsync();

            return true;
        }

        // ═══════════════════════════════════════════
        //  INVESTMENTS (GASTOS / INVERSIONES)
        // ═══════════════════════════════════════════

        public async Task<List<InvestmentDto>> GetInvestmentsAsync(int supplierId)
        {
            // Nota: Aquí usamos una proyección manual antes del MapToInvDto si fuera necesario,
            // pero como MapToInvDto es estático y simple, EF Core suele traducirlo bien en memoria.
            var investments = await _db.Investments
                .Where(i => i.SupplierId == supplierId)
                .OrderByDescending(i => i.Date)
                .ThenByDescending(i => i.CreatedAt)
                .ToListAsync();

            return investments.Select(i => MapToInvDto(i)).ToList();
        }

        public async Task<InvestmentDto> CreateInvestmentAsync(int supplierId, CreateInvestmentRequest request)
        {
            // 1. Validar que el proveedor exista
            var supplierExists = await _db.Suppliers.AnyAsync(s => s.Id == supplierId);
            if (!supplierExists)
                throw new InvalidOperationException($"Proveedor con Id {supplierId} no encontrado.");

            // 2. Lógica de Multi-Moneda 💵
            decimal finalRate = 1.0m;
            string finalCurrency = "MXN";

            // Normalizamos la entrada (quitamos espacios y pasamos a mayúsculas)
            if (!string.IsNullOrWhiteSpace(request.Currency))
            {
                finalCurrency = request.Currency.Trim().ToUpper();
            }

            if (finalCurrency == "USD")
            {
                // Si es Dólares, el tipo de cambio es OBLIGATORIO
                if (request.ExchangeRate == null || request.ExchangeRate <= 0)
                {
                    throw new InvalidOperationException("Para registros en USD, el Tipo de Cambio es obligatorio y debe ser mayor a 0.");
                }
                finalRate = request.ExchangeRate.Value;
            }
            else
            {
                // Si es cualquier otra cosa (o MXN), asumimos Pesos y TC = 1
                finalCurrency = "MXN";
                finalRate = 1.0m;
            }

            // 3. Crear entidad
            var investment = new Investment
            {
                SupplierId = supplierId,
                Amount = request.Amount,
                Date = DateTime.SpecifyKind(request.Date.Date.AddHours(12), DateTimeKind.Utc),
                Notes = request.Notes?.Trim(),
                CreatedAt = DateTime.UtcNow,

                // Nuevos campos guardados
                Currency = finalCurrency,
                ExchangeRate = finalRate
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
        //  MAPPERS (TRANSFORMADORES)
        // ═══════════════════════════════════════════

        private static SupplierDto MapToDto(Supplier s) => new(
            s.Id,
            s.Name,
            s.ContactName,
            s.Phone,
            s.Notes,
            s.CreatedAt
        );

        // Aquí agregamos los nuevos campos al DTO de salida
        private static InvestmentDto MapToInvDto(Investment i) => new(
            Id: i.Id,
            SupplierId: i.SupplierId,
            Amount: i.Amount,
            Date: i.Date,
            Notes: i.Notes,
            CreatedAt: i.CreatedAt,
            // Nuevos datos:
            Currency: i.Currency ?? "MXN", // Protección contra nulos viejos
            ExchangeRate: i.ExchangeRate == 0 ? 1 : i.ExchangeRate, // Protección contra ceros viejos
            TotalMXN: i.Amount * (i.ExchangeRate == 0 ? 1 : i.ExchangeRate) // Cálculo automático
        );
    }
}