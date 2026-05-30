using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
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
        private readonly string _frontendUrl;

        public LoyaltyController(AppDbContext db, IConfiguration config)
        {
            _db = db;
            _frontendUrl = config["App:FrontendUrl"] ?? "https://regibazar.com";
        }

        /// <summary>GET /api/loyalty/{clientId} - Resumen de puntos y nivel</summary>
        [HttpGet("{clientId}")]
        public async Task<IActionResult> GetAccountSummary(int clientId)
        {
            var client = await _db.Clients.FindAsync(clientId);

            if (client == null) return NotFound("La clienta no encontrada");

            var (tierKey, tierLabel) = ResolveTier(client.LifetimePoints);

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
                tier = tierLabel,
                tierKey,
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
                .Select(t => new { t.Id, t.Points, t.Reason, t.Date })
                .ToListAsync();

            return Ok(history);
        }

        /// <summary>GET /api/loyalty/rewards - Catálogo de premios canjeables (público para el link de la clienta)</summary>
        [AllowAnonymous]
        [HttpGet("rewards")]
        public async Task<ActionResult<List<LoyaltyRewardDto>>> GetRewards()
        {
            var rewards = await _db.LoyaltyRewards
                .Where(r => r.IsActive)
                .OrderBy(r => r.SortOrder)
                .ThenBy(r => r.PointsCost)
                .Select(r => new LoyaltyRewardDto(
                    r.Id, r.Name, r.Description, r.PointsCost, r.Type.ToString(), r.Value, r.Icon))
                .ToListAsync();

            return Ok(rewards);
        }

        /// <summary>
        /// POST /api/loyalty/redeem - Canjea un premio aplicándolo como descuento en un pedido activo.
        /// Lo usa la admin al cobrar. Resta los puntos y deja registro en el historial.
        /// </summary>
        [HttpPost("redeem")]
        public async Task<ActionResult<OrderSummaryDto>> Redeem([FromBody] RedeemRewardRequest req)
        {
            await using var tx = await _db.Database.BeginTransactionAsync();

            var order = await _db.Orders
                .Include(o => o.Client)
                .Include(o => o.Items)
                .Include(o => o.Payments)
                .Include(o => o.SalesPeriod)
                .FirstOrDefaultAsync(o => o.Id == req.OrderId);

            if (order == null) return NotFound("Pedido no encontrado");
            if (order.Client == null) return BadRequest("El pedido no tiene clienta asociada");
            if (order.ClientId != req.ClientId) return BadRequest("El pedido no pertenece a esa clienta");
            if (order.Status == Models.OrderStatus.Delivered || order.Status == Models.OrderStatus.Canceled)
                return BadRequest("Solo se puede canjear en pedidos que aún no se entregan ni cancelan.");

            var reward = await _db.LoyaltyRewards.FindAsync(req.RewardId);
            if (reward == null || !reward.IsActive) return NotFound("Premio no disponible");

            var client = order.Client;
            if (client.CurrentPoints < reward.PointsCost)
                return BadRequest($"Puntos insuficientes: la clienta tiene {client.CurrentPoints} y el premio cuesta {reward.PointsCost}.");

            // Calcular el descuento según el tipo de premio
            decimal discount = reward.Type switch
            {
                LoyaltyRewardType.FixedDiscount => reward.Value,
                LoyaltyRewardType.FreeShipping => order.ShippingCost,
                _ => 0m // Gift: regalo físico, sin descuento monetario
            };

            // Proteger ingresos: el descuento no puede superar lo que falta por cobrar del pedido
            var cobrableActual = order.Subtotal + order.ShippingCost - order.DiscountAmount;
            if (discount > cobrableActual)
                return BadRequest($"El pedido (${cobrableActual:0.00}) es menor al descuento del premio (${discount:0.00}).");

            // 1. Aplicar descuento al pedido (mismo mecanismo que el descuento de cumpleaños)
            order.DiscountAmount += discount;
            order.Total = Math.Max(0, order.Subtotal + order.ShippingCost - order.DiscountAmount);

            // 2. Restar puntos (CurrentPoints baja; LifetimePoints NO, para conservar el nivel)
            client.CurrentPoints -= reward.PointsCost;

            // 3. Dejar rastro en el historial (transparencia)
            _db.LoyaltyTransactions.Add(new LoyaltyTransaction
            {
                ClientId = client.Id,
                Points = -reward.PointsCost,
                Reason = $"Canje: {reward.Name} (pedido #{order.Id})",
                Date = DateTime.UtcNow
            });

            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            return Ok(ExcelService.MapToSummary(order, client, _frontendUrl));
        }

        /// <summary>POST /api/loyalty/adjust - Sumar o restar puntos manualmente (regalos o correcciones)</summary>
        [HttpPost("adjust")]
        public async Task<IActionResult> AdjustPoints([FromBody] AdjustPointsRequest req)
        {
            if (req.Points == 0) return BadRequest("Los puntos no pueden ser cero.");

            var client = await _db.Clients.FindAsync(req.ClientId);
            if (client == null) return NotFound("Clienta no encontrada.");

            if (req.Points < 0 && client.CurrentPoints + req.Points < 0)
            {
                return BadRequest($"La clienta solo tiene {client.CurrentPoints} puntos. No puedes restarle {Math.Abs(req.Points)}.");
            }

            _db.LoyaltyTransactions.Add(new LoyaltyTransaction
            {
                ClientId = client.Id,
                Points = req.Points,
                Reason = req.Reason.Trim(),
                Date = DateTime.UtcNow
            });

            client.CurrentPoints += req.Points;
            if (req.Points > 0)
            {
                client.LifetimePoints += req.Points;
            }

            await _db.SaveChangesAsync();

            return Ok(new { message = "Puntos ajustados correctamente.", newBalance = client.CurrentPoints });
        }

        /// <summary>Nivel VIP según puntos históricos. Devuelve clave estable + etiqueta para la UI.</summary>
        private static (string key, string label) ResolveTier(int lifetimePoints)
        {
            if (lifetimePoints >= 300) return ("diamante", "Clienta Diamante 💎");
            if (lifetimePoints >= 100) return ("rosegold", "Clienta Rose Gold 🌸");
            return ("pink", "Clienta Pink 🎀");
        }
    }

    // DTOs
    public record AdjustPointsRequest(int ClientId, int Points, string Reason);
    public record LoyaltyRewardDto(int Id, string Name, string? Description, int PointsCost, string Type, decimal Value, string? Icon);
    public record RedeemRewardRequest(int ClientId, int OrderId, int RewardId);
}
