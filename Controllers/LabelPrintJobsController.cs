using System.Security.Claims;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Controllers;

/// <summary>
/// Entrega el diseño publicado junto con datos ya normalizados para el renderer.
/// No envía entidades de negocio completas ni permite usar un borrador al imprimir.
/// </summary>
[ApiController]
[Route("api/label-print-jobs")]
[Authorize(Policy = "InventoryAccess")]
public sealed class LabelPrintJobsController(AppDbContext db, IConfiguration configuration) : ControllerBase
{
    private readonly string _frontendUrl = (configuration["App:FrontendUrl"] ?? "https://regibazar.com").TrimEnd('/');

    [HttpGet("{templateId:guid}/boxes/{boxId:guid}")]
    public async Task<ActionResult<LabelPrintContextDto>> GetBoxPrintContext(
        Guid templateId,
        Guid boxId,
        CancellationToken cancellationToken)
    {
        var template = await GetPublishedTemplateAsync(templateId, LabelTemplateKind.InventoryBox, cancellationToken);
        if (template is null) return TemplateNotAvailable();

        var box = await db.InventoryBoxes
            .AsNoTracking()
            .Include(current => current.Items)
            .FirstOrDefaultAsync(current => current.Id == boxId && !current.IsArchived, cancellationToken);
        if (box is null) return NotFound(new { message = "Caja no encontrada." });

        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["box.code"] = box.Code,
            ["box.name"] = box.Name,
            ["box.location"] = box.Location ?? string.Empty,
            ["box.nfcUrl"] = $"{_frontendUrl}/caja/{box.NfcToken}",
            ["box.articleTypes"] = box.Items.Count(item => item.Quantity > 0).ToString(),
            ["box.totalUnits"] = box.Items.Sum(item => item.Quantity).ToString(),
            ["box.updatedAt"] = box.UpdatedAt.ToString("O")
        };

        return Ok(MapContext(template, box.Id.ToString(), data));
    }

    [HttpGet("{templateId:guid}/items/{itemId:guid}")]
    public async Task<ActionResult<LabelPrintContextDto>> GetItemPrintContext(
        Guid templateId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var template = await GetPublishedTemplateAsync(templateId, LabelTemplateKind.InventoryItem, cancellationToken);
        if (template is null) return TemplateNotAvailable();

        var item = await db.InventoryItems
            .AsNoTracking()
            .Include(current => current.InventoryBox)
            .FirstOrDefaultAsync(current => current.Id == itemId && !current.InventoryBox.IsArchived, cancellationToken);
        if (item is null) return NotFound(new { message = "Artículo de inventario no encontrado." });

        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["item.name"] = item.Name,
            ["item.variant"] = item.Variant ?? string.Empty,
            ["item.barcode"] = item.Barcode ?? string.Empty,
            ["item.labelCode"] = item.LabelCode,
            ["item.scannableCode"] = item.Barcode ?? item.LabelCode,
            ["item.quantity"] = item.Quantity.ToString(),
            ["item.boxCode"] = item.InventoryBox.Code,
            ["item.boxName"] = item.InventoryBox.Name,
            ["item.location"] = item.InventoryBox.Location ?? string.Empty
        };

        return Ok(MapContext(template, item.Id.ToString(), data));
    }

    [HttpGet("{templateId:guid}/packages/{packageId:guid}")]
    public async Task<ActionResult<LabelPrintContextDto>> GetPackagePrintContext(
        Guid templateId,
        Guid packageId,
        CancellationToken cancellationToken)
    {
        var template = await GetPublishedTemplateAsync(templateId, LabelTemplateKind.OrderPackage, cancellationToken);
        if (template is null) return TemplateNotAvailable();

        var package = await db.OrderPackages
            .AsNoTracking()
            .Include(current => current.Order)
            .ThenInclude(order => order.Client)
            .Include(current => current.Order)
            .ThenInclude(order => order.Items)
            .Include(current => current.Order)
            .ThenInclude(order => order.Packages)
            .AsSplitQuery()
            .FirstOrDefaultAsync(current => current.Id == packageId, cancellationToken);
        if (package is null) return NotFound(new { message = "Bolsa no encontrada." });

        var order = package.Order;
        var summary = string.Join("\n", order.Items
            .OrderBy(item => item.Id)
            .Take(12)
            .Select(item => $"{item.Quantity} × {item.ProductName}"));
        if (order.Items.Count > 12) summary += $"\n+ {order.Items.Count - 12} artículo(s) más";

        var deliveryAddress = !string.IsNullOrWhiteSpace(order.AlternativeAddress)
            ? order.AlternativeAddress
            : order.Client.Address ?? string.Empty;
        var deliveryInstructions = !string.IsNullOrWhiteSpace(order.DeliveryInstructions)
            ? order.DeliveryInstructions
            : order.Client.DeliveryInstructions ?? string.Empty;

        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["order.id"] = order.Id.ToString(),
            ["order.clientName"] = order.Client.Name,
            ["order.phone"] = order.Client.Phone ?? string.Empty,
            ["order.address"] = deliveryAddress,
            ["order.deliveryInstructions"] = deliveryInstructions,
            ["order.itemSummary"] = summary,
            ["package.number"] = package.PackageNumber.ToString(),
            ["package.total"] = order.Packages.Count.ToString(),
            ["package.qrCodeValue"] = package.QrCodeValue,
            ["package.status"] = package.Status.ToString()
        };

        return Ok(MapContext(template, package.Id.ToString(), data));
    }

    [HttpPost("events")]
    public async Task<ActionResult> CreatePrintEvent(
        [FromBody] CreateLabelPrintEventDto request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.TargetId))
        {
            return BadRequest(new { message = "El objetivo de impresión es obligatorio." });
        }

        var version = await db.LabelTemplateVersions
            .Include(current => current.LabelTemplate)
            .FirstOrDefaultAsync(current => current.Id == request.LabelTemplateVersionId, cancellationToken);
        if (version is null || version.Status != LabelTemplateVersionStatus.Published || version.LabelTemplate.IsArchived)
        {
            return BadRequest(new { message = "La impresión debe usar una versión publicada y activa." });
        }
        if (version.LabelTemplate.Kind != request.TargetKind || version.LabelTemplate.PrinterProfile != request.PrinterProfile)
        {
            return BadRequest(new { message = "La plantilla no corresponde al objetivo o a la impresora seleccionada." });
        }
        if (!await TargetExistsAsync(request.TargetKind, request.TargetId.Trim(), cancellationToken))
        {
            return NotFound(new { message = "El elemento que se intentó imprimir ya no existe o no está activo." });
        }

        db.LabelPrintEvents.Add(new LabelPrintEvent
        {
            LabelTemplateVersionId = version.Id,
            TargetKind = request.TargetKind,
            TargetId = request.TargetId.Trim(),
            PrinterProfile = request.PrinterProfile,
            Method = request.Method,
            Copies = request.Copies,
            RequestedBy = CurrentUserName(),
            RequestedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
        return Accepted();
    }

    private async Task<LabelTemplateVersion?> GetPublishedTemplateAsync(
        Guid templateId,
        LabelTemplateKind expectedKind,
        CancellationToken cancellationToken) =>
        await db.LabelTemplateVersions
            .AsNoTracking()
            .Include(version => version.LabelTemplate)
            .FirstOrDefaultAsync(version =>
                version.LabelTemplateId == templateId &&
                version.Status == LabelTemplateVersionStatus.Published &&
                version.LabelTemplate.PublishedVersionId == version.Id &&
                version.LabelTemplate.IsDefault &&
                version.LabelTemplate.Kind == expectedKind &&
                !version.LabelTemplate.IsArchived,
                cancellationToken);

    private ActionResult<LabelPrintContextDto> TemplateNotAvailable() =>
        NotFound(new { message = "La plantilla publicada para esta impresión no está disponible." });

    private static LabelPrintContextDto MapContext(
        LabelTemplateVersion version,
        string targetId,
        Dictionary<string, string> data) =>
        new(
            new PublishedLabelTemplateDto(
                version.LabelTemplate.Id,
                version.LabelTemplate.Name,
                version.LabelTemplate.Kind.ToString(),
                version.LabelTemplate.PrinterProfile.ToString(),
                version.Id,
                version.VersionNumber,
                version.DesignJson),
            version.LabelTemplate.Kind.ToString(),
            targetId,
            data);

    private async Task<bool> TargetExistsAsync(LabelTemplateKind kind, string targetId, CancellationToken cancellationToken)
    {
        return kind switch
        {
            LabelTemplateKind.InventoryBox when Guid.TryParse(targetId, out var boxId) =>
                await db.InventoryBoxes.AnyAsync(box => box.Id == boxId && !box.IsArchived, cancellationToken),
            LabelTemplateKind.InventoryItem when Guid.TryParse(targetId, out var itemId) =>
                await db.InventoryItems.AnyAsync(item => item.Id == itemId && !item.InventoryBox.IsArchived, cancellationToken),
            LabelTemplateKind.OrderPackage when Guid.TryParse(targetId, out var packageId) =>
                await db.OrderPackages.AnyAsync(package => package.Id == packageId, cancellationToken),
            _ => false
        };
    }

    private string CurrentUserName() =>
        User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "Administración";
}
