using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Controllers;

public record UpdateClientRequest(
    string Name,
    string? Phone,
    string? Address,
    string Tag,
    string Type,
    string? DeliveryInstructions = null
);

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ClientsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IOrderService _orderService;

    public ClientsController(AppDbContext db, IOrderService orderService)
    {
        _db = db;
        _orderService = orderService;
    }

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
                    c.Type,
                    c.DeliveryInstructions
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
            c.Type,
            c.DeliveryInstructions
        )).ToList();

        return Ok(clients);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ClientDto>> GetById(int id)
    {
        var c = await _db.Clients
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
                c.Type,
                c.DeliveryInstructions
            })
            .FirstOrDefaultAsync(c => c.Id == id);

        if (c == null) return NotFound();

        return Ok(new ClientDto(c.Id, c.Name, c.Phone, c.Address, c.Tag.ToString(), c.OrdersCount, c.TotalSpent, c.Type, c.DeliveryInstructions));
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
        client.DeliveryInstructions = req.DeliveryInstructions;

        if (Enum.TryParse<ClientTag>(req.Tag, true, out var newTag))
        {
            client.Tag = newTag;
        }

        // 3. 🚀 MAGIA: Si el tipo cambió, recalculamos las caducidades pendientes
        if (typeChanged)
        {
            await _orderService.SyncOrderExpirationsAsync(id);
        }

        await _db.SaveChangesAsync();
        return NoContent();
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