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

        [HttpGet("financials")]
        public async Task<ActionResult<FinancialReportDto>> GetFinancialReport(
     [FromQuery] DateTime startDate,
     [FromQuery] DateTime endDate)
        {
            if (startDate > endDate)
                return BadRequest(new { message = "startDate no puede ser mayor que endDate." });

            try
            {
                var report = await _service.GetFinancialReportAsync(startDate, endDate);
                return Ok(report);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = ex.Message,
                    inner = ex.InnerException?.Message,
                    stack = ex.StackTrace
                });
            }
        }
    }
}
