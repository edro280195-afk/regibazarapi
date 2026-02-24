using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Controllers;

public record ClientDto(
    int Id,
    string Name,
    string? Phone,
    string? Address,
    string Tag,
    int OrdersCount,
    decimal TotalSpent,
    string? Type = null
);

public record UpdateClientRequest(
    string Name,
    string? Phone,
    string? Address,
    string Tag,
    string Type
);

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ClientsController : ControllerBase
{
    private readonly AppDbContext _db;

    public ClientsController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<List<ClientDto>>> GetAll()
    {
        var dbData = await _db.Clients
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Phone,
                c.Address,
                c.Tag,
                OrdersCount = c.Orders.Count(),
                TotalSpent = c.Orders
                    .Where(o => o.Status != Models.OrderStatus.Canceled)
                    .Sum(o => o.Total),
                    c.Type
            })
            .OrderByDescending(x => x.TotalSpent)
            .ToListAsync();

        var clients = dbData.Select(c => new ClientDto(
            c.Id,
            c.Name,
            c.Phone,
            c.Address,
            c.Tag.ToString(),
            c.OrdersCount,
            c.TotalSpent,
            c.Type
        )).ToList();

        return Ok(clients);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateClientRequest req)
    {
        var client = await _db.Clients.FindAsync(id);
        if (client == null) return NotFound();

        // 1. Detectamos si hubo un cambio de categoría antes de actualizar
        bool typeChanged = client.Type != req.Type;

        // 2. Actualizamos los datos de la clienta
        client.Name = req.Name;
        client.Phone = req.Phone;
        client.Address = req.Address;
        client.Type = req.Type;

        if (Enum.TryParse<ClientTag>(req.Tag, true, out var newTag))
        {
            client.Tag = newTag;
        }

        // 3. 🚀 MAGIA: Si el tipo cambió, recalculamos las caducidades pendientes
        if (typeChanged)
        {
            var pendingOrders = await _db.Orders
                .Where(o => o.ClientId == id && o.Status == Models.OrderStatus.Pending)
                .ToListAsync();

            if (pendingOrders.Any())
            {
                var mexicoZone = TimeZoneInfo.FindSystemTimeZoneById("America/Monterrey");

                foreach (var order in pendingOrders)
                {
                    // Convertimos la fecha en la que SE CREÓ el pedido a hora de Nuevo Laredo
                    var orderCreationMexicoTime = TimeZoneInfo.ConvertTimeFromUtc(order.CreatedAt, mexicoZone);

                    // Calculamos cuántos días faltaban para su primer lunes
                    int daysUntilMonday = (8 - (int)orderCreationMexicoTime.DayOfWeek) % 7;
                    if (daysUntilMonday == 0) daysUntilMonday = 7;

                    // Fecha base: El primer Lunes a las 00:00:00 desde que se creó el pedido
                    DateTime localExpiration = orderCreationMexicoTime.Date.AddDays(daysUntilMonday);

                    // Si la ascendieron a Frecuente, le sumamos 7 días a esa base
                    if (req.Type == "Frecuente")
                    {
                        localExpiration = localExpiration.AddDays(7);
                    }

                    // Guardamos la nueva caducidad en la base de datos (en formato UTC)
                    order.ExpiresAt = TimeZoneInfo.ConvertTimeToUtc(localExpiration, mexicoZone);
                }
            }
        }

        await _db.SaveChangesAsync();
        return Ok(client);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var client = await _db.Clients.FindAsync(id);

        if (client == null) return NotFound();

        _db.Clients.Remove(client);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return BadRequest("No se puede eliminar el cliente porque tiene pedidos asociados. Borra los pedidos primero.");
        }
        return NoContent();
    }

    [HttpDelete("wipe")]
    public async Task<IActionResult> WipeAllClients()
    {
        await _db.OrderItems.ExecuteDeleteAsync();
        await _db.Deliveries.ExecuteDeleteAsync();
        await _db.Orders.ExecuteDeleteAsync();

        await _db.Clients.ExecuteDeleteAsync();

        return NoContent();
    }
}