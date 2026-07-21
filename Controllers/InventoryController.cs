using System.Security.Cryptography;
using System.Security.Claims;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Controllers;

/// <summary>
/// Inventario físico de cajas. No comparte ni modifica el catálogo o el POS legado.
/// </summary>
[ApiController]
[Route("api/inventory")]
[Authorize]
public class InventoryController(AppDbContext db, IConfiguration configuration) : ControllerBase
{
    private readonly string _frontendUrl = (configuration["App:FrontendUrl"] ?? "https://regibazar.com").TrimEnd('/');

    /// <summary>Lista cajas activas y permite encontrarlas por caja, ubicación o artículo.</summary>
    [HttpGet("boxes")]
    public async Task<ActionResult<List<InventoryBoxSummaryDto>>> GetBoxes(
        [FromQuery] string? search,
        CancellationToken cancellationToken)
    {
        var normalizedSearch = search?.Trim().ToLowerInvariant();
        var query = db.InventoryBoxes
            .AsNoTracking()
            .Where(box => !box.IsArchived);

        if (!string.IsNullOrWhiteSpace(normalizedSearch))
        {
            query = query.Where(box =>
                box.Code.ToLower().Contains(normalizedSearch) ||
                box.Name.ToLower().Contains(normalizedSearch) ||
                (box.Location ?? string.Empty).ToLower().Contains(normalizedSearch) ||
                box.Items.Any(item =>
                    item.Name.ToLower().Contains(normalizedSearch) ||
                    (item.Variant ?? string.Empty).ToLower().Contains(normalizedSearch) ||
                    (item.Barcode ?? string.Empty).ToLower().Contains(normalizedSearch)));
        }

        var boxes = await query
            .OrderBy(box => box.Code)
            .Select(box => new InventoryBoxSummaryDto(
                box.Id,
                box.Code,
                box.Name,
                box.Location,
                box.IsNfcBound,
                box.Items.Count(item => item.Quantity > 0),
                box.Items.Sum(item => (int?)item.Quantity) ?? 0,
                box.UpdatedAt))
            .ToListAsync(cancellationToken);

        return Ok(boxes);
    }

    /// <summary>Obtiene una caja con su contenido e historial reciente.</summary>
    [HttpGet("boxes/{id:guid}")]
    public async Task<ActionResult<InventoryBoxDto>> GetBox(Guid id, CancellationToken cancellationToken)
    {
        var box = await LoadBoxAsync(id, cancellationToken);
        return box is null ? NotFound(new { message = "Caja no encontrada." }) : Ok(MapBox(box));
    }

    /// <summary>Resuelve la caja que se encuentra dentro de una URL NFC.</summary>
    [HttpGet("boxes/by-token/{token}")]
    public async Task<ActionResult<InventoryBoxDto>> GetBoxByToken(string token, CancellationToken cancellationToken)
    {
        var box = await db.InventoryBoxes
            .Include(current => current.Items)
            .Include(current => current.Movements)
            .ThenInclude(movement => movement.InventoryItem)
            .FirstOrDefaultAsync(current => current.NfcToken == token && !current.IsArchived, cancellationToken);

        return box is null ? NotFound(new { message = "Esta etiqueta no está vinculada a una caja activa." }) : Ok(MapBox(box));
    }

    /// <summary>Crea una caja y genera la URL que se escribirá en su etiqueta NFC.</summary>
    [HttpPost("boxes")]
    public async Task<ActionResult<InventoryBoxDto>> CreateBox(
        [FromBody] CreateInventoryBoxDto request,
        CancellationToken cancellationToken)
    {
        var code = request.Code.Trim();
        var name = request.Name.Trim();
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
            return BadRequest(new { message = "El código y el nombre de la caja son obligatorios." });

        var codeExists = await db.InventoryBoxes
            .AnyAsync(box => !box.IsArchived && box.Code.ToLower() == code.ToLower(), cancellationToken);
        if (codeExists)
            return Conflict(new { message = "Ya existe una caja activa con ese código." });

        var box = new InventoryBox
        {
            Code = code,
            Name = name,
            Location = NormalizeNullable(request.Location),
            NfcToken = GenerateNfcToken(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.InventoryBoxes.Add(box);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(GetBox), new { id = box.Id }, MapBox(box));
    }

    /// <summary>Edita el nombre y la ubicación de una caja.</summary>
    [HttpPut("boxes/{id:guid}")]
    public async Task<ActionResult<InventoryBoxDto>> UpdateBox(
        Guid id,
        [FromBody] UpdateInventoryBoxDto request,
        CancellationToken cancellationToken)
    {
        var box = await db.InventoryBoxes.FirstOrDefaultAsync(current => current.Id == id && !current.IsArchived, cancellationToken);
        if (box is null) return NotFound(new { message = "Caja no encontrada." });

        var code = request.Code.Trim();
        var name = request.Name.Trim();
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
            return BadRequest(new { message = "El código y el nombre de la caja son obligatorios." });

        var codeExists = await db.InventoryBoxes.AnyAsync(current =>
            current.Id != id && !current.IsArchived && current.Code.ToLower() == code.ToLower(), cancellationToken);
        if (codeExists) return Conflict(new { message = "Ya existe una caja activa con ese código." });

        box.Code = code;
        box.Name = name;
        box.Location = NormalizeNullable(request.Location);
        box.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var reloaded = await LoadBoxAsync(id, cancellationToken);
        return Ok(MapBox(reloaded!));
    }

    /// <summary>Confirma que la URL fue escrita y verificada en una etiqueta NFC.</summary>
    [HttpPost("boxes/{id:guid}/bind-nfc")]
    public async Task<ActionResult<InventoryBoxDto>> BindNfc(
        Guid id,
        [FromBody] BindInventoryNfcDto request,
        CancellationToken cancellationToken)
    {
        var box = await db.InventoryBoxes.FirstOrDefaultAsync(current => current.Id == id && !current.IsArchived, cancellationToken);
        if (box is null) return NotFound(new { message = "Caja no encontrada." });

        var uid = request.TagUid.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(uid)) return BadRequest(new { message = "No se pudo leer el identificador de la etiqueta." });

        var boundElsewhere = await db.InventoryBoxes.AnyAsync(current =>
            current.Id != id && current.NfcTagUid == uid && !current.IsArchived, cancellationToken);
        if (boundElsewhere) return Conflict(new { message = "Esta etiqueta ya está vinculada a otra caja." });

        box.NfcTagUid = uid;
        box.IsNfcBound = true;
        box.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var reloaded = await LoadBoxAsync(id, cancellationToken);
        return Ok(MapBox(reloaded!));
    }

    /// <summary>Agrega un artículo a una caja o incrementa el que coincide.</summary>
    [HttpPost("boxes/{id:guid}/items")]
    public async Task<ActionResult<InventoryBoxDto>> AddItem(
        Guid id,
        [FromBody] CreateInventoryItemDto request,
        CancellationToken cancellationToken)
    {
        if (request.Quantity <= 0) return BadRequest(new { message = "La cantidad debe ser mayor que cero." });
        var name = request.Name.Trim();
        if (string.IsNullOrWhiteSpace(name)) return BadRequest(new { message = "El nombre del artículo es obligatorio." });

        var box = await db.InventoryBoxes
            .Include(current => current.Items)
            .FirstOrDefaultAsync(current => current.Id == id && !current.IsArchived, cancellationToken);
        if (box is null) return NotFound(new { message = "Caja no encontrada." });

        var variant = NormalizeNullable(request.Variant);
        var barcode = NormalizeNullable(request.Barcode);
        var item = box.Items.FirstOrDefault(current =>
            current.Name.ToLower() == name.ToLower() &&
            (current.Variant ?? string.Empty).ToLower() == (variant ?? string.Empty).ToLower() &&
            (current.Barcode ?? string.Empty).ToLower() == (barcode ?? string.Empty).ToLower());

        var isNew = item is null;
        if (item is null)
        {
            item = new InventoryItem
            {
                InventoryBoxId = box.Id,
                Name = name,
                Variant = variant,
                Barcode = barcode,
                Quantity = 0,
                CreatedAt = DateTime.UtcNow
            };
            db.InventoryItems.Add(item);
        }

        item.Quantity += request.Quantity;
        item.UpdatedAt = DateTime.UtcNow;
        box.UpdatedAt = DateTime.UtcNow;
        db.InventoryMovements.Add(new InventoryMovement
        {
            InventoryBoxId = box.Id,
            InventoryItemId = item.Id,
            Type = isNew ? InventoryMovementType.InitialCount : InventoryMovementType.Added,
            QuantityDelta = request.Quantity,
            QuantityAfter = item.Quantity,
            Note = NormalizeNullable(request.Note),
            PerformedBy = CurrentUserName(),
            OccurredAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync(cancellationToken);
        var reloaded = await LoadBoxAsync(id, cancellationToken);
        return Ok(MapBox(reloaded!));
    }

    /// <summary>Ajusta una existencia sin permitir cantidades negativas.</summary>
    [HttpPost("items/{id:guid}/adjust")]
    public async Task<ActionResult<InventoryBoxDto>> AdjustItem(
        Guid id,
        [FromBody] AdjustInventoryItemDto request,
        CancellationToken cancellationToken)
    {
        if (request.QuantityDelta == 0) return BadRequest(new { message = "El ajuste debe modificar la cantidad." });

        var item = await db.InventoryItems
            .Include(current => current.InventoryBox)
            .FirstOrDefaultAsync(current => current.Id == id && !current.InventoryBox.IsArchived, cancellationToken);
        if (item is null) return NotFound(new { message = "Artículo no encontrado." });
        if (item.Quantity + request.QuantityDelta < 0)
            return BadRequest(new { message = "No puedes sacar más artículos de los que hay en la caja." });

        item.Quantity += request.QuantityDelta;
        item.UpdatedAt = DateTime.UtcNow;
        item.InventoryBox.UpdatedAt = DateTime.UtcNow;
        db.InventoryMovements.Add(new InventoryMovement
        {
            InventoryBoxId = item.InventoryBoxId,
            InventoryItemId = item.Id,
            Type = request.QuantityDelta > 0 ? InventoryMovementType.Added : InventoryMovementType.Removed,
            QuantityDelta = request.QuantityDelta,
            QuantityAfter = item.Quantity,
            Note = NormalizeNullable(request.Note),
            PerformedBy = CurrentUserName(),
            OccurredAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync(cancellationToken);
        var reloaded = await LoadBoxAsync(item.InventoryBoxId, cancellationToken);
        return Ok(MapBox(reloaded!));
    }

    /// <summary>Mueve artículos entre dos cajas dentro de una sola transacción.</summary>
    [HttpPost("transfers")]
    public async Task<ActionResult<InventoryBoxDto>> Transfer(
        [FromBody] TransferInventoryItemsDto request,
        CancellationToken cancellationToken)
    {
        if (request.SourceBoxId == request.DestinationBoxId)
            return BadRequest(new { message = "El origen y el destino deben ser cajas distintas." });
        if (request.Quantity <= 0) return BadRequest(new { message = "La cantidad debe ser mayor que cero." });

        var sourceItem = await db.InventoryItems
            .Include(item => item.InventoryBox)
            .FirstOrDefaultAsync(item => item.Id == request.ItemId && item.InventoryBoxId == request.SourceBoxId && !item.InventoryBox.IsArchived, cancellationToken);
        if (sourceItem is null) return NotFound(new { message = "El artículo de origen no fue encontrado." });
        if (sourceItem.Quantity < request.Quantity)
            return BadRequest(new { message = "La caja de origen no tiene suficientes artículos." });

        var destination = await db.InventoryBoxes
            .Include(box => box.Items)
            .FirstOrDefaultAsync(box => box.Id == request.DestinationBoxId && !box.IsArchived, cancellationToken);
        if (destination is null) return NotFound(new { message = "La caja destino no fue encontrada." });

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var destinationItem = destination.Items.FirstOrDefault(item =>
            item.Name.ToLower() == sourceItem.Name.ToLower() &&
            (item.Variant ?? string.Empty).ToLower() == (sourceItem.Variant ?? string.Empty).ToLower() &&
            (item.Barcode ?? string.Empty).ToLower() == (sourceItem.Barcode ?? string.Empty).ToLower());
        if (destinationItem is null)
        {
            destinationItem = new InventoryItem
            {
                InventoryBoxId = destination.Id,
                Name = sourceItem.Name,
                Variant = sourceItem.Variant,
                Barcode = sourceItem.Barcode,
                Quantity = 0,
                CreatedAt = DateTime.UtcNow
            };
            db.InventoryItems.Add(destinationItem);
        }

        sourceItem.Quantity -= request.Quantity;
        sourceItem.UpdatedAt = DateTime.UtcNow;
        destinationItem.Quantity += request.Quantity;
        destinationItem.UpdatedAt = DateTime.UtcNow;
        sourceItem.InventoryBox.UpdatedAt = DateTime.UtcNow;
        destination.UpdatedAt = DateTime.UtcNow;

        var groupId = Guid.NewGuid();
        var note = NormalizeNullable(request.Note);
        db.InventoryMovements.AddRange(
            new InventoryMovement
            {
                InventoryBoxId = sourceItem.InventoryBoxId,
                InventoryItemId = sourceItem.Id,
                TransferGroupId = groupId,
                Type = InventoryMovementType.TransferOut,
                QuantityDelta = -request.Quantity,
                QuantityAfter = sourceItem.Quantity,
                Note = note,
                PerformedBy = CurrentUserName(),
                OccurredAt = DateTime.UtcNow
            },
            new InventoryMovement
            {
                InventoryBoxId = destination.Id,
                InventoryItemId = destinationItem.Id,
                TransferGroupId = groupId,
                Type = InventoryMovementType.TransferIn,
                QuantityDelta = request.Quantity,
                QuantityAfter = destinationItem.Quantity,
                Note = note,
                PerformedBy = CurrentUserName(),
                OccurredAt = DateTime.UtcNow
            });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var reloaded = await LoadBoxAsync(destination.Id, cancellationToken);
        return Ok(MapBox(reloaded!));
    }

    private async Task<InventoryBox?> LoadBoxAsync(Guid id, CancellationToken cancellationToken) =>
        await db.InventoryBoxes
            .Include(box => box.Items)
            .Include(box => box.Movements)
            .ThenInclude(movement => movement.InventoryItem)
            .AsSplitQuery()
            .FirstOrDefaultAsync(box => box.Id == id && !box.IsArchived, cancellationToken);

    private InventoryBoxDto MapBox(InventoryBox box) => new(
        box.Id,
        box.Code,
        box.Name,
        box.Location,
        box.IsNfcBound,
        $"{_frontendUrl}/caja/{box.NfcToken}",
        box.Items.OrderBy(item => item.Name).ThenBy(item => item.Variant)
            .Select(item => new InventoryItemDto(item.Id, item.Name, item.Variant, item.Barcode, item.Quantity, item.UpdatedAt)).ToList(),
        box.Movements.OrderByDescending(movement => movement.OccurredAt).Take(30)
            .Select(movement => new InventoryMovementDto(
                movement.Id,
                movement.InventoryItemId,
                movement.InventoryItem?.Name,
                movement.Type.ToString(),
                movement.QuantityDelta,
                movement.QuantityAfter,
                movement.Note,
                movement.PerformedBy,
                movement.OccurredAt)).ToList(),
        box.CreatedAt,
        box.UpdatedAt);

    private string CurrentUserName() =>
        User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "Administración";

    private static string? NormalizeNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string GenerateNfcToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
