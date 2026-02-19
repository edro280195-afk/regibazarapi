using EntregasApi.Data;
using EntregasApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class LoyaltyController : ControllerBase
    {
        private readonly AppDbContext _db;

        public LoyaltyController(AppDbContext db)
        {
            _db = db;
        }

        /// <summary>GET /api/loyalty/{clientId} - Obtiene el resumen de puntos y nivel</summary>
        [HttpGet("{clientId}")]
        public async Task<IActionResult> GetAccountSummary(int clientId)
        {
            var client = await _db.Clients.FindAsync(clientId);

            if (client == null) return NotFound("La clienta no encontrada");

            string tier = "Clienta Pink";
            if (client.LifetimePoints >= 300) tier = "Clienta Diamante 💎";
            else if (client.LifetimePoints >= 100) tier = "Clienta Rose Gold 🌸";

            var lastAccrual = await _db.LoyaltyTransactions
                .Where(t => t.ClientId == clientId && t.Points > 0)
                .OrderByDescending(t => t.Date)
                .Select(t => t.Date)
                .FirstOrDefaultAsync();

            return Ok(new
            {
                clientId = client.Id,
                clientName = client.Name,
                currentPoints = client.CurrentPoints,
                lifetimePoints = client.LifetimePoints,
                tier = tier,
                lastAccrual = lastAccrual != default ? lastAccrual : (DateTime?)null
            });
        }
        /// <summary>GET /api/loyalty/{clientId}/history - Historial de transacciones</summary>
        [HttpGet("{clientId}/history")]
        public async Task<IActionResult> GetTransactionHistory(int clientId)
        {
            var history = await _db.LoyaltyTransactions
                .Where(t => t.ClientId == clientId)
                .OrderByDescending(t => t.Date)
                .Select(t => new
                {
                    t.Id,
                    t.Points,
                    t.Reason,
                    t.Date
                })
                .ToListAsync();

            return Ok(history);
        }

        /// <summary>POST /api/loyalty/adjust - Sumar o restar puntos manualmente (Canjes o Regalos)</summary>
        [HttpPost("adjust")]
        public async Task<IActionResult> AdjustPoints([FromBody] AdjustPointsRequest req)
        {
            if (req.Points == 0) return BadRequest("Los puntos no pueden ser cero.");

            var client = await _db.Clients.FindAsync(req.ClientId);
            if (client == null) return NotFound("Clienta no encontrada.");

            // Validar que no quede en saldo negativo si va a gastar
            if (req.Points < 0 && client.CurrentPoints + req.Points < 0)
            {
                return BadRequest($"La clienta solo tiene {client.CurrentPoints} puntos. No puedes restarle {Math.Abs(req.Points)}.");
            }

            // 1. Crear la transacción en el historial
            var transaction = new LoyaltyTransaction
            {
                ClientId = client.Id,
                Points = req.Points,
                Reason = req.Reason.Trim(),
                Date = DateTime.UtcNow
            };
            _db.LoyaltyTransactions.Add(transaction);

            // 2. Actualizar el saldo de la clienta
            client.CurrentPoints += req.Points;

            // Si le estamos regalando puntos (positivo), también sube su histórico VIP
            if (req.Points > 0)
            {
                client.LifetimePoints += req.Points;
            }

            await _db.SaveChangesAsync();

            return Ok(new
            {
                message = "Puntos ajustados correctamente.",
                newBalance = client.CurrentPoints
            });
        }
    }

    // DTO para la petición
    public record AdjustPointsRequest(int ClientId, int Points, string Reason);
}
