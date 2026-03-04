using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public class SalesPeriodService : ISalesPeriodService
{
    private readonly AppDbContext _db;

    public SalesPeriodService(AppDbContext db) => _db = db;

    // ═══════════════════════════════════════════
    //  GET ALL
    // ═══════════════════════════════════════════

    public async Task<List<SalesPeriodDto>> GetAllAsync()
    {
        return await _db.SalesPeriods
            .OrderByDescending(p => p.IsActive)
            .ThenByDescending(p => p.StartDate)
            .Select(p => new SalesPeriodDto(
                p.Id, p.Name, p.StartDate, p.EndDate, p.IsActive, p.CreatedAt
            ))
            .ToListAsync();
    }

    // ═══════════════════════════════════════════
    //  CREATE
    // ═══════════════════════════════════════════

    public async Task<SalesPeriodDto> CreateAsync(CreateSalesPeriodRequest req)
    {
        var period = new SalesPeriod
        {
            Name      = req.Name.Trim(),
            StartDate = DateTime.SpecifyKind(req.StartDate, DateTimeKind.Utc),
            EndDate   = DateTime.SpecifyKind(req.EndDate,   DateTimeKind.Utc),
            IsActive  = false,
            CreatedAt = DateTime.UtcNow
        };

        _db.SalesPeriods.Add(period);
        await _db.SaveChangesAsync();

        return new SalesPeriodDto(period.Id, period.Name, period.StartDate, period.EndDate, period.IsActive, period.CreatedAt);
    }

    // ═══════════════════════════════════════════
    //  ACTIVATE  — apaga todos, enciende 1
    // ═══════════════════════════════════════════

    public async Task<SalesPeriodDto?> ActivateAsync(int id)
    {
        var period = await _db.SalesPeriods.FindAsync(id);
        if (period is null) return null;

        // Apagar todos con ExecuteUpdate (sin cargar en RAM)
        await _db.SalesPeriods
            .Where(p => p.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, false));

        period.IsActive = true;
        await _db.SaveChangesAsync();

        return new SalesPeriodDto(period.Id, period.Name, period.StartDate, period.EndDate, period.IsActive, period.CreatedAt);
    }

    // ═══════════════════════════════════════════
    //  PERIOD REPORT  — 100 % SQL, sin ToList previo
    // ═══════════════════════════════════════════

    public async Task<PeriodReportDto?> GetPeriodReportAsync(int id)
    {
        var period = await _db.SalesPeriods
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id);

        if (period is null) return null;

        // ── Suma de ventas entregadas (SQL SUM) ──
        var totalSales = await _db.Orders
            .Where(o => o.SalesPeriodId == id && o.Status == EntregasApi.Models.OrderStatus.Delivered)
            .SumAsync(o => (decimal?)o.Total) ?? 0m;

        // ── Suma de inversiones en pesos (SQL SUM) ──
        var totalInvestments = await _db.Investments
            .Where(i => i.SalesPeriodId == id)
            .SumAsync(i => (decimal?)(i.Amount * i.ExchangeRate)) ?? 0m;

        // ── Desglose por proveedor (proyección directa, sin cargar entidades) ──
        var bySupplier = await _db.Investments
            .Where(i => i.SalesPeriodId == id)
            .Include(i => i.Supplier)
            .GroupBy(i => i.Supplier.Name)
            .Select(g => new PeriodInvestmentBySupplierDto(
                g.Key,
                g.Sum(i => i.Amount * i.ExchangeRate),
                g.Count()
            ))
            .OrderByDescending(x => x.TotalInvested)
            .ToListAsync();

        return new PeriodReportDto(
            id,
            period.Name,
            totalSales,
            totalInvestments,
            totalSales - totalInvestments,
            bySupplier
        );
    }

    public async Task<int> SyncRelatedEntitiesAsync(int id, SyncSalesPeriodRequest request)
    {
        var period = await _db.SalesPeriods.AnyAsync(p => p.Id == id);
        if (!period) return 0;

        // Pedidos: Que caigan en el rango de Órdenes y no tengan Id asignado
        var ordersUpdated = await _db.Orders
            .Where(o => o.SalesPeriodId == null && o.CreatedAt >= request.OrderStartDate && o.CreatedAt <= request.OrderEndDate)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.SalesPeriodId, id));

        // Inversiones: Que caigan en el rango de Inversiones y no tengan Id asignado
        var investmentsUpdated = await _db.Investments
            .Where(i => i.SalesPeriodId == null && i.Date >= request.InvStartDate && i.Date <= request.InvEndDate)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.SalesPeriodId, id));

        return ordersUpdated + investmentsUpdated;
    }
}
