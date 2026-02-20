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
    string Tag
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

        client.Name = req.Name;
        client.Phone = req.Phone;
        client.Address = req.Address;

        if (Enum.TryParse<ClientTag>(req.Tag, true, out var newTag))
        {
            client.Tag = newTag;
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