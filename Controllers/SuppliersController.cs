using EntregasApi.DTOs;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EntregasApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class SuppliersController : ControllerBase
    {
        private readonly ISuppliersService _service;

        public SuppliersController(ISuppliersService service)
        {
            _service = service;
        }

        // ═══════════════════════════════════════════
        //  SUPPLIERS CRUD
        // ═══════════════════════════════════════════

        /// <summary>GET api/suppliers</summary>
        [HttpGet]
        public async Task<ActionResult<List<SupplierDto>>> GetAll()
        {
            var list = await _service.GetAllSuppliersAsync();
            return Ok(list);
        }

        /// <summary>GET api/suppliers/{id}</summary>
        [HttpGet("{id}")]
        public async Task<ActionResult<SupplierDto>> GetById(int id)
        {
            var supplier = await _service.GetSupplierByIdAsync(id);
            if (supplier is null) return NotFound();
            return Ok(supplier!);
        }

        /// <summary>POST api/suppliers</summary>
        [HttpPost]
        public async Task<ActionResult<SupplierDto>> Create([FromBody] CreateSupplierRequest request)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var created = await _service.CreateSupplierAsync(request);
            return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
        }

        /// <summary>PUT api/suppliers/{id}</summary>
        [HttpPut("{id}")]
        public async Task<ActionResult<SupplierDto>> Update(int id, [FromBody] UpdateSupplierRequest request)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var updated = await _service.UpdateSupplierAsync(id, request);
            if (updated == null) return NotFound();
            return Ok(updated);
        }

        /// <summary>DELETE api/suppliers/{id}</summary>
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            var deleted = await _service.DeleteSupplierAsync(id);
            if (!deleted) return NotFound();
            return NoContent();
        }

        // ═══════════════════════════════════════════
        //  INVESTMENTS CRUD
        // ═══════════════════════════════════════════

        /// <summary>GET api/suppliers/{supplierId}/investments</summary>
        [HttpGet("{supplierId}/investments")]
        public async Task<ActionResult<List<InvestmentDto>>> GetInvestments(int supplierId)
        {
            var list = await _service.GetInvestmentsAsync(supplierId);
            return Ok(list);
        }

        /// <summary>POST api/suppliers/{supplierId}/investments</summary>
        [HttpPost("{supplierId}/investments")]
        public async Task<ActionResult<InvestmentDto>> AddInvestment(int supplierId, [FromBody] CreateInvestmentRequest request)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            try
            {
                var created = await _service.CreateInvestmentAsync(supplierId, request);
                return Created($"api/suppliers/{supplierId}/investments/{created.Id}", created);
            }
            catch (InvalidOperationException ex)
            {
                return NotFound(new { message = ex.Message });
            }
        }

        /// <summary>DELETE api/suppliers/{supplierId}/investments/{investmentId}</summary>
        [HttpDelete("{supplierId}/investments/{investmentId}")]
        public async Task<IActionResult> DeleteInvestment(int supplierId, int investmentId)
        {
            var deleted = await _service.DeleteInvestmentAsync(supplierId, investmentId);
            if (!deleted) return NotFound();
            return NoContent();
        }
    }
}
