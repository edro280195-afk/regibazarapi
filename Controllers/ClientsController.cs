using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models; // Asegúrate de tener esto para el Enum
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Controllers;

// TUS DTOs SE QUEDAN IGUAL
public record ClientDto(
    int Id,
    string Name,
    string? Phone,
    string? Address,
    string Tag,
    int OrdersCount,
    decimal TotalSpent
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
        // PASO 1: Consulta a Base de Datos
        // Usamos un tipo anónimo (Select new { ... }) para que EF Core
        // pueda traducir el cálculo de la suma y el ordenamiento a SQL puro.
        var dbData = await _db.Clients
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Phone,
                c.Address,
                c.Tag, // Traemos el Enum tal cual (sin ToString todavía)
                OrdersCount = c.Orders.Count(),
                // Calculamos el total gastado directamente en la BD
                TotalSpent = c.Orders
                    .Where(o => o.Status != Models.OrderStatus.Canceled)
                    .Sum(o => o.Total)
            })
            .OrderByDescending(x => x.TotalSpent) // Ahora sí, SQL entiende por qué columna ordenar
            .ToListAsync();

        // PASO 2: Transformación en Memoria
        // Ahora convertimos los resultados crudos a tu DTO bonito.
        // Aquí es seguro usar .ToString() y otras lógicas de C#.
        var clients = dbData.Select(c => new ClientDto(
            c.Id,
            c.Name,
            c.Phone,
            c.Address,
            c.Tag.ToString(), // Convertimos el Enum a String aquí
            c.OrdersCount,
            c.TotalSpent
        )).ToList();

        return Ok(clients);
    }

    // EL PUT (UPDATE) SE QUEDA IGUAL, YA ESTABA BIEN
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

    /// <summary>DELETE /api/clients/wipe - BORRADO TOTAL (Clientas + Sus Pedidos)</summary>
    [HttpDelete("wipe")]
    // [Authorize(Roles = "Admin")] // Recomendado
    public async Task<IActionResult> WipeAllClients()
    {
        // PASO 1: Limpieza de dependencias (para evitar errores de llave foránea)
        // Borramos primero lo más profundo: Items -> Entregas -> Órdenes
        await _db.OrderItems.ExecuteDeleteAsync();
        await _db.Deliveries.ExecuteDeleteAsync();
        await _db.Orders.ExecuteDeleteAsync();

        // PASO 2: Ahora sí, borramos todas las clientas
        await _db.Clients.ExecuteDeleteAsync();

        return NoContent();
    }
}