using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services
{
    public class ExpenseService : IExpenseService
    {
        private readonly AppDbContext _db;
        private readonly IWebHostEnvironment _env;

        public ExpenseService(AppDbContext db, IWebHostEnvironment env)
        {
            _db = db;
            _env = env;
        }

        // ═══════════════════════════════════════════
        //  CREATE EXPENSE (Driver side)
        // ═══════════════════════════════════════════

        public async Task<DriverExpenseDto> CreateExpenseAsync(string driverToken, CreateDriverExpenseRequest request, IFormFile? photo)
        {
            var route = await _db.DeliveryRoutes
                .FirstOrDefaultAsync(r => r.DriverToken == driverToken);

            if (route == null)
                throw new InvalidOperationException("Ruta no encontrada con ese token.");

            var expense = new DriverExpense
            {
                DeliveryRouteId = route.Id,
                Amount = request.Amount,
                ExpenseType = request.ExpenseType,
                Notes = request.Notes?.Trim(),
                Date = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            };

            if (photo != null && photo.Length > 0)
            {
                expense.EvidencePath = await SaveExpensePhoto(photo, route.Id);
            }

            _db.DriverExpenses.Add(expense);
            await _db.SaveChangesAsync();

            return MapToDto(expense, null);
        }

        // ═══════════════════════════════════════════
        //  GET EXPENSES (Admin side, by quincena)
        // ═══════════════════════════════════════════

        public async Task<List<DriverExpenseDto>> GetExpensesByPeriodAsync(string? period)
        {
            IQueryable<DriverExpense> query = _db.DriverExpenses
                .Include(e => e.DeliveryRoute)
                .OrderByDescending(e => e.Date);

            if (!string.IsNullOrEmpty(period))
            {
                var (start, end) = ParseQuincena(period);
                query = query.Where(e => e.Date >= start && e.Date < end);
            }

            var expenses = await query.ToListAsync();

            return expenses.Select(e => MapToDto(e, e.DeliveryRoute)).ToList();
        }

        // ═══════════════════════════════════════════
        //  FINANCIAL REPORT (Admin)
        // ═══════════════════════════════════════════

        public async Task<FinancialReportDto> GetFinancialReportAsync(DateTime startDate, DateTime endDate)
        {
            startDate = DateTime.SpecifyKind(startDate.Date, DateTimeKind.Utc);
            endDate = DateTime.SpecifyKind(endDate.Date, DateTimeKind.Utc);
            var endDateExclusive = endDate.AddDays(1);

            // Ingresos: Ordenes entregadas en el rango
            var deliveredOrders = await _db.Orders
                .Include(o => o.Client)
                .Where(o => o.Status == Models.OrderStatus.Delivered
                            && o.CreatedAt >= startDate
                            && o.CreatedAt < endDateExclusive)
                .ToListAsync();

            var totalIncome = deliveredOrders.Sum(o => o.Total);

            // Inversiones en el rango
            var investments = await _db.Investments
                .Include(i => i.Supplier)
                .Where(i => i.Date >= startDate && i.Date < endDateExclusive)
                .OrderByDescending(i => i.Date)
                .ToListAsync();

            var totalInvestment = investments.Sum(i => i.Amount);

            // Gastos del repartidor en el rango
            var expenses = await _db.DriverExpenses
                .Include(e => e.DeliveryRoute)
                .Where(e => e.Date >= startDate && e.Date < endDateExclusive)
                .OrderByDescending(e => e.Date)
                .ToListAsync();

            var totalExpenses = expenses.Sum(e => e.Amount);

            // Mapear detalles
            var incomeLines = deliveredOrders.Select(o => new IncomeLineDto(
                o.Id,
                o.Client?.Name ?? "Sin cliente",
                o.Total,
                o.OrderType.ToString(),
                o.CreatedAt
            )).ToList();

            var investmentLines = investments.Select(i => new InvestmentLineDto(
                i.Id,
                i.Supplier?.Name ?? "Sin proveedor",
                i.Amount,
                i.Date,
                i.Notes
            )).ToList();

            var expenseLines = expenses.Select(e => new ExpenseLineDto(
                e.Id,
                null,
                e.Amount,
                e.ExpenseType,
                e.Date,
                e.Notes,
                BuildEvidenceUrl(e.EvidencePath)
            )).ToList();

            return new FinancialReportDto
            {
                Period = $"{startDate:yyyy-MM-dd} a {endDate:yyyy-MM-dd}",
                StartDate = startDate,
                EndDate = endDate,
                TotalIncome = totalIncome,
                TotalInvestment = totalInvestment,
                TotalExpenses = totalExpenses,
                NetProfit = totalIncome - totalInvestment - totalExpenses,
                Details = new FinancialDetailsDto
                {
                    Incomes = incomeLines,
                    Investments = investmentLines,
                    Expenses = expenseLines
                }
            };
        }

        // ═══════════════════════════════════════════
        //  HELPERS
        // ═══════════════════════════════════════════

        private async Task<string> SaveExpensePhoto(IFormFile photo, int routeId)
        {
            var uploadDir = Path.Combine(_env.ContentRootPath, "uploads", "expenses");
            Directory.CreateDirectory(uploadDir);

            var fileName = $"r{routeId}_{Guid.NewGuid():N}{Path.GetExtension(photo.FileName)}";
            var filePath = Path.Combine(uploadDir, fileName);

            using var stream = new FileStream(filePath, FileMode.Create);
            await photo.CopyToAsync(stream);

            return $"expenses/{fileName}";
        }

        private static string? BuildEvidenceUrl(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            return $"/uploads/{path}";
        }

        /// <summary>
        /// Parsea quincena con formato "2025-Q1" a "2025-Q24"
        /// Q1=Ene 1-15, Q2=Ene 16-31, Q3=Feb 1-15 ... Q24=Dic 16-31
        /// </summary>
        private static (DateTime start, DateTime end) ParseQuincena(string period)
        {
            var parts = period.Split('-');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var year)
                || !parts[1].StartsWith('Q') || !int.TryParse(parts[1][1..], out var q)
                || q < 1 || q > 24)
            {
                throw new ArgumentException($"Formato de periodo inválido: '{period}'. Usa 'YYYY-QN' donde N es 1-24.");
            }

            int month = (q + 1) / 2; // Q1,Q2->1  Q3,Q4->2  ...  Q23,Q24->12
            bool firstHalf = (q % 2 == 1); // Q impar = primera quincena

            DateTime start;
            DateTime end;

            if (firstHalf)
            {
                start = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
                end = new DateTime(year, month, 16, 0, 0, 0, DateTimeKind.Utc);
            }
            else
            {
                start = new DateTime(year, month, 16, 0, 0, 0, DateTimeKind.Utc);
                end = new DateTime(year, month, DateTime.DaysInMonth(year, month), 0, 0, 0, DateTimeKind.Utc).AddDays(1);
            }

            return (start, end);
        }

        private static DriverExpenseDto MapToDto(DriverExpense e, DeliveryRoute? route)
        {
            return new DriverExpenseDto(
                e.Id,
                e.DeliveryRouteId,
                null,
                e.Amount,
                e.ExpenseType,
                e.Date,
                e.Notes,
                BuildEvidenceUrl(e.EvidencePath),
                e.CreatedAt
            );
        }
    }
}
