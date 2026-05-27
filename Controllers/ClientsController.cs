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
    private readonly IGeocodingService _geocoding;

    public ClientsController(AppDbContext db, IOrderService orderService, IGeocodingService geocoding)
    {
        _db = db;
        _orderService = orderService;
        _geocoding = geocoding;
    }

    /// <summary>
    /// POST /api/clients/bulk-geocode - Resuelve lat/lng para los clientes recibidos cuando tienen
    /// dirección pero faltan coordenadas. Persiste el resultado en BD. Devuelve detalle por cliente.
    /// </summary>
    [HttpPost("bulk-geocode")]
    public async Task<ActionResult<List<BulkGeocodeResultDto>>> BulkGeocode([FromBody] BulkGeocodeRequest req)
    {
        if (req.ClientIds == null || req.ClientIds.Count == 0)
            return Ok(new List<BulkGeocodeResultDto>());

        var ids = req.ClientIds.Distinct().ToList();
        var clients = await _db.Clients.Where(c => ids.Contains(c.Id)).ToListAsync();

        var results = new List<BulkGeocodeResultDto>();
        foreach (var c in clients)
        {
            if (c.Latitude.HasValue && c.Longitude.HasValue)
            {
                results.Add(new BulkGeocodeResultDto(c.Id, true, c.Latitude, c.Longitude, c.Address, null));
                continue;
            }
            if (string.IsNullOrWhiteSpace(c.Address))
            {
                results.Add(new BulkGeocodeResultDto(c.Id, false, null, null, null, "Sin dirección"));
                continue;
            }

            var r = await _geocoding.GeocodeAsync(c.Address);
            if (r.Success && r.Latitude.HasValue && r.Longitude.HasValue)
            {
                c.Latitude = r.Latitude;
                c.Longitude = r.Longitude;
                results.Add(new BulkGeocodeResultDto(c.Id, true, r.Latitude, r.Longitude, r.FormattedAddress, null));
            }
            else
            {
                results.Add(new BulkGeocodeResultDto(c.Id, false, null, null, null, r.Error ?? r.Status));
            }
        }

        await _db.SaveChangesAsync();
        return Ok(results);
    }

    /// <summary>
    /// POST /api/clients/{id}/set-coordinates - Guarda lat/lng explícitas (uso del map picker).
    /// </summary>
    [HttpPost("{id:int}/set-coordinates")]
    public async Task<IActionResult> SetCoordinates(int id, [FromBody] SetClientCoordinatesRequest req)
    {
        var c = await _db.Clients.FindAsync(id);
        if (c == null) return NotFound();
        c.Latitude = req.Latitude;
        c.Longitude = req.Longitude;
        if (!string.IsNullOrWhiteSpace(req.Address)) c.Address = req.Address;
        await _db.SaveChangesAsync();
        return NoContent();
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
                    c.DeliveryInstructions,
                    c.Latitude,
                    c.Longitude
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
            c.DeliveryInstructions,
            Latitude: c.Latitude,
            Longitude: c.Longitude
        )).ToList();

        return Ok(clients);
    }

    [HttpGet("{id:int}")]
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
                c.DeliveryInstructions,
                c.Latitude,
                c.Longitude
            })
            .FirstOrDefaultAsync(c => c.Id == id);

        if (c == null) return NotFound();

        return Ok(new ClientDto(c.Id, c.Name, c.Phone, c.Address, c.Tag.ToString(), c.OrdersCount, c.TotalSpent, c.Type, c.DeliveryInstructions, Latitude: c.Latitude, Longitude: c.Longitude));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateClientRequest req)
    {
        var client = await _db.Clients.FindAsync(id);
        if (client == null) return NotFound();

        // 1. Detectamos si hubo un cambio de categoría antes de actualizar
        bool typeChanged = client.Type != req.Type;

        // 2. Actualizamos los datos de la clienta
        // Los campos opcionales se ignoran si llegan vacíos/null: este endpoint se usa
        // desde formularios parciales (guardar solo dirección, solo tag, etc.) y antes
        // borraba los datos previamente capturados.
        client.Name = req.Name;
        client.NormalizedName = TextNormalizer.NormalizeName(req.Name);
        if (!string.IsNullOrWhiteSpace(req.Phone))
        {
            client.Phone = req.Phone;
            client.NormalizedPhone = TextNormalizer.NormalizePhone(req.Phone);
        }
        if (!string.IsNullOrWhiteSpace(req.Address))
        {
            client.Address = req.Address;
            client.NormalizedAddress = TextNormalizer.NormalizeAddress(req.Address);
        }
        client.Type = req.Type;
        if (!string.IsNullOrWhiteSpace(req.DeliveryInstructions)) client.DeliveryInstructions = req.DeliveryInstructions;

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

    [HttpDelete("{id:int}")]
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
        await _db.ClientAliases.ExecuteDeleteAsync();

        await _db.Clients.ExecuteDeleteAsync();

        return NoContent();
    }
}