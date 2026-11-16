using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Controllers;

/// <summary>
/// Campanita (recordatorio privado) por pedido. No toca la fecha de entrega programada
/// ni el vencimiento del enlace de la clienta: es solo un aviso para la dueña.
/// El aviso lo dispara <see cref="OrderRemindersService"/>.
/// </summary>
[ApiController]
[Route("api")]
[Authorize]
public class OrderRemindersController : ControllerBase
{
    private readonly AppDbContext _db;

    public OrderRemindersController(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>GET /api/order-reminders - Campanitas de pedidos que aún no se entregan ni cancelan</summary>
    [HttpGet("order-reminders")]
    public async Task<ActionResult<List<OrderReminderDto>>> GetActive()
    {
        var reminders = await _db.OrderReminders
            .AsNoTracking()
            .Where(r => r.Order.Status != OrderStatus.Delivered && r.Order.Status != OrderStatus.Canceled)
            .OrderBy(r => r.RemindAt)
            .ToListAsync();

        return Ok(reminders.Select(ToDto).ToList());
    }

    /// <summary>PUT /api/orders/{orderId}/reminder - Crea o reprograma la campanita del pedido</summary>
    [HttpPut("orders/{orderId:int}/reminder")]
    public async Task<ActionResult<OrderReminderDto>> Save(int orderId, [FromBody] SaveOrderReminderRequest req)
    {
        var status = await _db.Orders
            .AsNoTracking()
            .Where(o => o.Id == orderId)
            .Select(o => (OrderStatus?)o.Status)
            .FirstOrDefaultAsync();

        if (status == null) return NotFound("Pedido no encontrado 😿");
        if (!OrderReminderPolicy.IsOrderActionable(status.Value))
            return BadRequest("Este pedido ya fue entregado o cancelado, no necesita campanita.");

        var now = DateTime.UtcNow;
        var targetAt = OrderReminderPolicy.NormalizeUtc(req.TargetAt);

        var error = OrderReminderPolicy.Validate(targetAt, req.LeadMinutes, req.Note, now);
        if (error != null) return BadRequest(error);

        var reminder = await _db.OrderReminders.FirstOrDefaultAsync(r => r.OrderId == orderId);
        if (reminder == null)
        {
            reminder = new OrderReminder { OrderId = orderId, CreatedAt = now };
            _db.OrderReminders.Add(reminder);
        }

        reminder.TargetAt = targetAt;
        reminder.LeadMinutes = req.LeadMinutes;
        reminder.RemindAt = OrderReminderPolicy.ComputeRemindAt(targetAt, req.LeadMinutes);
        reminder.Note = OrderReminderPolicy.CleanNote(req.Note);
        reminder.SentAt = null; // Reprogramar siempre vuelve a armar el aviso
        reminder.UpdatedAt = now;

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Doble clic: dos altas simultáneas chocan con el índice único por pedido.
            return Conflict("La campanita ya se estaba guardando. Intenta de nuevo.");
        }

        return Ok(ToDto(reminder));
    }

    /// <summary>DELETE /api/orders/{orderId}/reminder - Quita la campanita (idempotente)</summary>
    [HttpDelete("orders/{orderId:int}/reminder")]
    public async Task<IActionResult> Delete(int orderId)
    {
        var reminder = await _db.OrderReminders.FirstOrDefaultAsync(r => r.OrderId == orderId);
        if (reminder != null)
        {
            _db.OrderReminders.Remove(reminder);
            await _db.SaveChangesAsync();
        }

        return NoContent();
    }

    private static OrderReminderDto ToDto(OrderReminder r) => new(
        r.Id, r.OrderId, r.TargetAt, r.LeadMinutes, r.RemindAt, r.Note, r.SentAt);
}
