using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using System.Globalization;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ReportsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ISalesPeriodService _periodService;

    public ReportsController(AppDbContext db, ISalesPeriodService periodService)
    {
        _db = db;
        _periodService = periodService;
    }

    /// <summary>GET /api/reports/period/{id} — Reporte exacto por Corte de Venta</summary>
    [HttpGet("period/{id:int}")]
    public async Task<ActionResult<PeriodReportDto>> GetPeriodReport(int id)
    {
        var report = await _periodService.GetPeriodReportAsync(id);
        if (report is null) return NotFound(new { message = $"Corte con Id {id} no encontrado." });
        return Ok(report);
    }

    /// <summary>GET /api/reports/glow-up-current-month</summary>
    [HttpGet("glow-up-current-month")]
    public async Task<ActionResult<GlowUpReportDto>> GlowUpCurrentMonth()
    {
        try
        {
            // 🚀 EL BLINDAJE: Forzamos la fecha a UTC universal para evitar el enojo de Postgres
            var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc);
            var startOfMonth = DateTime.SpecifyKind(new DateTime(now.Year, now.Month, 1), DateTimeKind.Utc);

            // Entregas del mes
            var deliveredOrders = await _db.Orders
                .Include(o => o.Items)
                .Where(o => o.Status == Models.OrderStatus.Delivered && o.CreatedAt >= startOfMonth)
                .ToListAsync();

            var totalDeliveries = deliveredOrders.Count;

            // Top producto (por cantidad vendida) - Manejo seguro de nulos
            var topProduct = deliveredOrders
                .SelectMany(o => o.Items)
                .GroupBy(i => i.ProductName)
                .Select(g => new { Name = g.Key, Qty = g.Sum(i => i.Quantity) })
                .OrderByDescending(g => g.Qty)
                .FirstOrDefault()?.Name ?? "Sorpresa ✨";

            // Nuevas clientas del mes
            var newClients = await _db.Clients
                .CountAsync(c => c.CreatedAt >= startOfMonth && c.Type=="Nueva");

            // Nombre del mes en español
            var culture = new CultureInfo("es-MX");
            var monthName = culture.TextInfo.ToTitleCase(now.ToString("MMMM", culture));

            return Ok(new GlowUpReportDto(
                monthName,
                totalDeliveries,
                topProduct,
                newClients
            ));
        }
        catch (Exception ex)
        {
            // 🚨 Si truena, que nos diga exactamente por qué en la consola, sin tirar la app entera
            Console.WriteLine($"Error en GlowUp: {ex.Message}");
            return StatusCode(500, "Hubo un error al generar la magia. Revisa la consola del servidor.");
        }
    }
}