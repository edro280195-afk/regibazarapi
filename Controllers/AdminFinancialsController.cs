using EntregasApi.DTOs;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EntregasApi.Controllers
{
    [ApiController]
    [Route("api/admin")]
    [Authorize]
    public class AdminFinancialsController : ControllerBase
    {
        private readonly IExpenseService _service;

        public AdminFinancialsController(IExpenseService service)
        {
            _service = service;
        }

        /// <summary>
        /// GET api/admin/expenses?period=2025-Q1
        /// Lista gastos del repartidor, filtrados por quincena (opcional)
        /// </summary>
        [HttpGet("expenses")]
        public async Task<ActionResult<List<DriverExpenseDto>>> GetExpenses([FromQuery] string? period)
        {
            try
            {
                var list = await _service.GetExpensesByPeriodAsync(period);
                return Ok(list);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// GET api/admin/financials?startDate=2025-01-01&endDate=2025-01-15
        /// Reporte financiero consolidado: ingresos, inversiones, gastos, utilidad neta
        /// </summary>
        [HttpGet("financials")]
        public async Task<ActionResult<FinancialReportDto>> GetFinancialReport(
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate)
        {
            if (startDate > endDate)
                return BadRequest(new { message = "startDate no puede ser mayor que endDate." });

            var report = await _service.GetFinancialReportAsync(startDate, endDate);
            return Ok(report);
        }

        // ═══════════════════════════════════════════
        //  ADMIN CRUD EXPENSES
        // ═══════════════════════════════════════════

        [HttpPost("expenses")]
        public async Task<ActionResult<DriverExpenseDto>> CreateExpense([FromBody] CreateAdminExpenseRequest request)
        {
            try
            {
                var expense = await _service.CreateAdminExpenseAsync(request);
                return Ok(expense);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPut("expenses/{id}")]
        public async Task<ActionResult<DriverExpenseDto>> UpdateExpense(int id, [FromBody] UpdateAdminExpenseRequest request)
        {
            try
            {
                var expense = await _service.UpdateExpenseAsync(id, request);
                return Ok(expense);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpDelete("expenses/{id}")]
        public async Task<IActionResult> DeleteExpense(int id)
        {
            var success = await _service.DeleteExpenseAsync(id);
            if (!success) return NotFound();
            return Ok(new { message = "Gasto eliminado correctamente." });
        }
    }
}
