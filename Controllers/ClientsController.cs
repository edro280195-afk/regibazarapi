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
    string? DeliveryInstructions = null,
    string? FacebookProfileUrl = null
);

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ClientsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IOrderService _orderService;
    private readonly IGeocodingService _geocoding;
    private readonly IClientResolverService _resolver;

    public ClientsController(AppDbContext db, IOrderService orderService, IGeocodingService geocoding, IClientResolverService resolver)
    {
        _db = db;
        _orderService = orderService;
        _geocoding = geocoding;
        _resolver = resolver;
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
    /// POST /api/clients/facebook-import/preview - Recibe filas (nombre + enlace FB) y las cruza
    /// con las clientas existentes usando matching difuso. Devuelve propuestas para revisión humana.
    /// NO modifica nada.
    /// </summary>
    [HttpPost("facebook-import/preview")]
    public async Task<ActionResult<FacebookImportPreviewResponse>> FacebookImportPreview([FromBody] FacebookImportPreviewRequest req)
    {
        var rows = req.Rows ?? new List<FacebookImportRow>();
        var items = new List<FacebookImportPreviewItem>();

        // Detectar enlaces duplicados dentro del mismo lote (posible error de captura)
        var urlCounts = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.FacebookUrl))
            .GroupBy(r => r.FacebookUrl.Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.Count());

        // Primera pasada: resolver candidatas por nombre
        var draft = new List<(int Index, FacebookImportRow Row, ResolveClientResponse Resolved)>();
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var name = (row.Name ?? string.Empty).Trim();
            ResolveClientResponse resolved = string.IsNullOrWhiteSpace(name)
                ? new ResolveClientResponse(new List<ResolveCandidateDto>(), "create")
                : await _resolver.ResolveAsync(name, null, null);
            draft.Add((i, row, resolved));
        }

        // Consultar de un jalón qué clientas top ya tienen Facebook (para avisar sobreescritura)
        var topClientIds = draft
            .Where(d => d.Resolved.Candidates.Count > 0)
            .Select(d => d.Resolved.Candidates[0].ClientId)
            .Distinct()
            .ToList();
        var clientsWithFb = await _db.Clients
            .Where(c => topClientIds.Contains(c.Id) && c.FacebookProfileUrl != null && c.FacebookProfileUrl != "")
            .Select(c => c.Id)
            .ToListAsync();
        var fbSet = new HashSet<int>(clientsWithFb);

        foreach (var (index, row, resolved) in draft)
        {
            var url = (row.FacebookUrl ?? string.Empty).Trim();
            var urlValid = FacebookLinkHelper.LooksLikeFacebookRef(url);
            var top = resolved.Candidates.FirstOrDefault();

            // "use" → match claro; "choose" → ambiguo; "create" → sin match confiable
            var status = resolved.SuggestedAction switch
            {
                "use" => "matched",
                "choose" => "review",
                _ => "notfound"
            };

            var dupInBatch = !string.IsNullOrWhiteSpace(url)
                && urlCounts.TryGetValue(url.ToLowerInvariant(), out var c) && c > 1;

            items.Add(new FacebookImportPreviewItem(
                RowIndex: index,
                InputName: row.Name ?? string.Empty,
                InputUrl: url,
                UrlValid: urlValid,
                Status: status,
                SuggestedClientId: status == "matched" ? top?.ClientId : null,
                TopScore: top?.Score ?? 0,
                TopAlreadyHasFacebook: top != null && fbSet.Contains(top.ClientId),
                DuplicateUrlInBatch: dupInBatch,
                Candidates: resolved.Candidates));
        }

        return Ok(new FacebookImportPreviewResponse(items));
    }

    /// <summary>
    /// POST /api/clients/facebook-import/apply - Guarda los enlaces ya confirmados por el usuario.
    /// Solo vincula clientas existentes; nunca crea nuevas.
    /// </summary>
    [HttpPost("facebook-import/apply")]
    public async Task<ActionResult<FacebookImportApplyResponse>> FacebookImportApply([FromBody] FacebookImportApplyRequest req)
    {
        var rows = req.Rows ?? new List<FacebookImportApplyRow>();
        var errors = new List<string>();
        int applied = 0, skipped = 0;

        // Quedarnos con la última asignación por clienta si viniera repetida
        var byClient = rows
            .Where(r => r.ClientId > 0)
            .GroupBy(r => r.ClientId)
            .ToDictionary(g => g.Key, g => g.Last().FacebookUrl);

        var ids = byClient.Keys.ToList();
        var clients = await _db.Clients.Where(c => ids.Contains(c.Id)).ToListAsync();
        var clientMap = clients.ToDictionary(c => c.Id);

        foreach (var (clientId, rawUrl) in byClient)
        {
            var url = (rawUrl ?? string.Empty).Trim();
            if (!FacebookLinkHelper.LooksLikeFacebookRef(url))
            {
                skipped++;
                errors.Add($"Clienta #{clientId}: enlace inválido, se omitió.");
                continue;
            }
            if (!clientMap.TryGetValue(clientId, out var client))
            {
                skipped++;
                errors.Add($"Clienta #{clientId}: no encontrada, se omitió.");
                continue;
            }
            client.FacebookProfileUrl = url;
            applied++;
        }

        await _db.SaveChangesAsync();
        return Ok(new FacebookImportApplyResponse(applied, skipped, errors));
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
        var normalizedAddress = ClientDataPolicy.NormalizeOptionalAddress(req.Address);
        if (normalizedAddress != null)
        {
            c.Address = normalizedAddress;
            c.NormalizedAddress = TextNormalizer.NormalizeAddress(normalizedAddress);
        }
        if (req.DeliveryInstructions != null)
        {
            c.DeliveryInstructions = string.IsNullOrWhiteSpace(req.DeliveryInstructions)
                ? null
                : req.DeliveryInstructions.Trim();
        }
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
            client.Address = req.Address.Trim();
            client.NormalizedAddress = TextNormalizer.NormalizeAddress(client.Address);
        }
        client.Type = req.Type;
        if (!string.IsNullOrWhiteSpace(req.DeliveryInstructions)) client.DeliveryInstructions = req.DeliveryInstructions;
        if (req.FacebookProfileUrl != null) client.FacebookProfileUrl = string.IsNullOrWhiteSpace(req.FacebookProfileUrl) ? null : req.FacebookProfileUrl;

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
