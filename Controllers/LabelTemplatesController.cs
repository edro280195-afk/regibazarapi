using System.Security.Claims;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Controllers;

/// <summary>
/// Biblioteca y control de versiones del diseñador de etiquetas. Sólo se publica
/// un diseño validado; el borrador sigue disponible para el siguiente ajuste.
/// </summary>
[ApiController]
[Route("api/label-templates")]
[Authorize(Policy = "InventoryAccess")]
public sealed class LabelTemplatesController(
    AppDbContext db,
    ILabelTemplateDesignValidator designValidator,
    ICloudinaryService cloudinaryService) : ControllerBase
{
    private const long MaxAssetSizeBytes = 5 * 1024 * 1024;

    [HttpGet]
    public async Task<ActionResult<List<LabelTemplateSummaryDto>>> GetTemplates(
        [FromQuery] bool includeArchived,
        CancellationToken cancellationToken)
    {
        var query = db.LabelTemplates
            .AsNoTracking()
            .Include(template => template.PublishedVersion)
            .AsQueryable();

        if (!includeArchived)
        {
            query = query.Where(template => !template.IsArchived);
        }

        var templates = await query
            .OrderBy(template => template.Kind)
            .ThenBy(template => template.Name)
            .Select(template => new LabelTemplateSummaryDto(
                template.Id,
                template.Name,
                template.Description,
                template.Kind.ToString(),
                template.PrinterProfile.ToString(),
                template.IsDefault,
                template.IsArchived,
                template.PublishedVersionId,
                template.PublishedVersion == null ? null : template.PublishedVersion.VersionNumber,
                template.UpdatedAt))
            .ToListAsync(cancellationToken);

        return Ok(templates);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<LabelTemplateDetailDto>> GetTemplate(Guid id, CancellationToken cancellationToken)
    {
        var template = await LoadTemplateAsync(id, cancellationToken);
        return template is null
            ? NotFound(new { message = "Plantilla no encontrada." })
            : Ok(MapDetail(template));
    }

    [HttpPost]
    public async Task<ActionResult<LabelTemplateDetailDto>> CreateTemplate(
        [FromBody] CreateLabelTemplateDto request,
        CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest(new { message = "El nombre de la plantilla es obligatorio." });
        }

        if (!LabelTemplateProfilePolicy.IsSupported(request.Kind, request.PrinterProfile))
        {
            return BadRequest(new { message = LabelTemplateProfilePolicy.GetUnsupportedMessage(request.Kind) });
        }

        var designJson = string.IsNullOrWhiteSpace(request.DesignJson)
            ? LabelTemplateDesignFactory.CreateDefaultDesign(request.Kind, request.PrinterProfile)
            : request.DesignJson.Trim();
        var validation = designValidator.Validate(designJson, request.Kind, request.PrinterProfile);
        var validationError = await BuildValidationErrorAsync(validation, cancellationToken);
        if (validationError is not null) return validationError;

        var existingName = await db.LabelTemplates.AnyAsync(template =>
            !template.IsArchived && template.Name.ToLower() == name.ToLower(), cancellationToken);
        if (existingName)
        {
            return Conflict(new { message = "Ya existe una plantilla activa con ese nombre." });
        }

        var now = DateTime.UtcNow;
        var template = new LabelTemplate
        {
            Name = name,
            Description = NormalizeNullable(request.Description),
            Kind = request.Kind,
            PrinterProfile = request.PrinterProfile,
            // Un borrador nunca se vuelve operativo. La primera publicación del
            // tipo selecciona la predeterminada de forma atómica.
            IsDefault = false,
            CreatedBy = CurrentUserName(),
            UpdatedBy = CurrentUserName(),
            CreatedAt = now,
            UpdatedAt = now
        };
        template.Versions.Add(new LabelTemplateVersion
        {
            VersionNumber = 1,
            Status = LabelTemplateVersionStatus.Draft,
            DesignJson = designJson,
            Revision = 1,
            CreatedBy = CurrentUserName(),
            CreatedAt = now
        });

        db.LabelTemplates.Add(template);
        await db.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetTemplate), new { id = template.Id }, MapDetail(template));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<LabelTemplateDetailDto>> UpdateTemplate(
        Guid id,
        [FromBody] UpdateLabelTemplateDto request,
        CancellationToken cancellationToken)
    {
        var template = await LoadTemplateAsync(id, cancellationToken);
        if (template is null || template.IsArchived) return NotFound(new { message = "Plantilla no encontrada." });

        var name = request.Name.Trim();
        if (string.IsNullOrWhiteSpace(name)) return BadRequest(new { message = "El nombre de la plantilla es obligatorio." });

        var nameExists = await db.LabelTemplates.AnyAsync(current =>
            current.Id != id && !current.IsArchived && current.Name.ToLower() == name.ToLower(), cancellationToken);
        if (nameExists) return Conflict(new { message = "Ya existe una plantilla activa con ese nombre." });

        template.Name = name;
        template.Description = NormalizeNullable(request.Description);
        template.UpdatedBy = CurrentUserName();
        template.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(MapDetail(template));
    }

    [HttpPut("{id:guid}/draft")]
    public async Task<ActionResult<SaveLabelTemplateDraftResultDto>> SaveDraft(
        Guid id,
        [FromBody] SaveLabelTemplateDraftDto request,
        CancellationToken cancellationToken)
    {
        var template = await LoadTemplateAsync(id, cancellationToken);
        if (template is null || template.IsArchived) return NotFound(new { message = "Plantilla no encontrada." });

        var draft = template.Versions.SingleOrDefault(version => version.Status == LabelTemplateVersionStatus.Draft);
        if (draft is null) return Conflict(new { message = "La plantilla no tiene un borrador disponible." });
        if (draft.Revision != request.ExpectedRevision)
        {
            return Conflict(new
            {
                message = "Esta plantilla fue modificada en otra sesión. Recarga el borrador antes de guardar.",
                currentRevision = draft.Revision,
                draft = MapVersion(draft)
            });
        }

        var designJson = request.DesignJson.Trim();
        var validation = designValidator.Validate(designJson, template.Kind, template.PrinterProfile);
        var validationError = await BuildValidationErrorAsync(validation, cancellationToken);
        if (validationError is not null) return validationError;

        draft.DesignJson = designJson;
        draft.Revision++;
        template.UpdatedBy = CurrentUserName();
        template.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return Ok(new SaveLabelTemplateDraftResultDto(MapVersion(draft), validation.Warnings.ToList()));
    }

    [HttpPost("{id:guid}/publish")]
    public async Task<ActionResult<PublishLabelTemplateResultDto>> Publish(
        Guid id,
        [FromBody] PublishLabelTemplateDto request,
        CancellationToken cancellationToken)
    {
        var template = await LoadTemplateAsync(id, cancellationToken);
        if (template is null || template.IsArchived) return NotFound(new { message = "Plantilla no encontrada." });

        var draft = template.Versions.SingleOrDefault(version => version.Status == LabelTemplateVersionStatus.Draft);
        if (draft is null) return Conflict(new { message = "La plantilla no tiene un borrador disponible." });
        if (draft.Revision != request.ExpectedRevision)
        {
            return Conflict(new { message = "El borrador cambió en otra sesión. Recarga antes de publicar.", currentRevision = draft.Revision });
        }

        var validation = designValidator.Validate(draft.DesignJson, template.Kind, template.PrinterProfile);
        var validationError = await BuildValidationErrorAsync(validation, cancellationToken);
        if (validationError is not null) return validationError;

        var now = DateTime.UtcNow;
        foreach (var previousVersion in template.Versions.Where(version => version.Status == LabelTemplateVersionStatus.Published))
        {
            previousVersion.Status = LabelTemplateVersionStatus.Archived;
        }

        draft.Status = LabelTemplateVersionStatus.Published;
        draft.PublishedAt = now;
        draft.PublishedBy = CurrentUserName();
        template.PublishedVersionId = draft.Id;
        if (!template.IsDefault)
        {
            var hasPublishedDefault = await db.LabelTemplates.AnyAsync(current =>
                current.Id != template.Id &&
                current.Kind == template.Kind &&
                current.IsDefault &&
                !current.IsArchived &&
                current.PublishedVersionId != null,
                cancellationToken);
            template.IsDefault = !hasPublishedDefault;
        }
        template.UpdatedAt = now;
        template.UpdatedBy = CurrentUserName();

        var nextDraft = new LabelTemplateVersion
        {
            LabelTemplateId = template.Id,
            VersionNumber = template.Versions.Max(version => version.VersionNumber) + 1,
            Status = LabelTemplateVersionStatus.Draft,
            DesignJson = draft.DesignJson,
            Revision = 1,
            CreatedBy = CurrentUserName(),
            CreatedAt = now
        };
        template.Versions.Add(nextDraft);

        await db.SaveChangesAsync(cancellationToken);
        return Ok(new PublishLabelTemplateResultDto(
            MapVersion(draft),
            MapVersion(nextDraft),
            validation.Warnings.ToList()));
    }

    [HttpPost("{id:guid}/versions/{versionId:guid}/restore")]
    public async Task<ActionResult<SaveLabelTemplateDraftResultDto>> RestoreVersion(
        Guid id,
        Guid versionId,
        [FromBody] RestoreLabelTemplateVersionDto request,
        CancellationToken cancellationToken)
    {
        var template = await LoadTemplateAsync(id, cancellationToken);
        if (template is null || template.IsArchived) return NotFound(new { message = "Plantilla no encontrada." });

        var source = template.Versions.SingleOrDefault(version => version.Id == versionId);
        var draft = template.Versions.SingleOrDefault(version => version.Status == LabelTemplateVersionStatus.Draft);
        if (source is null || draft is null) return NotFound(new { message = "No encontramos la versión o el borrador solicitados." });
        if (draft.Revision != request.ExpectedRevision)
        {
            return Conflict(new { message = "El borrador cambió en otra sesión. Recarga antes de restaurar.", currentRevision = draft.Revision });
        }

        var validation = designValidator.Validate(source.DesignJson, template.Kind, template.PrinterProfile);
        var validationError = await BuildValidationErrorAsync(validation, cancellationToken);
        if (validationError is not null) return validationError;

        draft.DesignJson = source.DesignJson;
        draft.Revision++;
        template.UpdatedBy = CurrentUserName();
        template.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new SaveLabelTemplateDraftResultDto(MapVersion(draft), validation.Warnings.ToList()));
    }

    [HttpPost("{id:guid}/duplicate")]
    public async Task<ActionResult<LabelTemplateDetailDto>> Duplicate(Guid id, CancellationToken cancellationToken)
    {
        var source = await LoadTemplateAsync(id, cancellationToken);
        if (source is null) return NotFound(new { message = "Plantilla no encontrada." });

        var sourceVersion = source.PublishedVersion ?? source.Versions.SingleOrDefault(version => version.Status == LabelTemplateVersionStatus.Draft);
        if (sourceVersion is null) return Conflict(new { message = "La plantilla no tiene un diseño que se pueda duplicar." });

        var now = DateTime.UtcNow;
        var copy = new LabelTemplate
        {
            Name = await CreateCopyNameAsync(source.Name, cancellationToken),
            Description = source.Description,
            Kind = source.Kind,
            PrinterProfile = source.PrinterProfile,
            IsDefault = false,
            CreatedBy = CurrentUserName(),
            UpdatedBy = CurrentUserName(),
            CreatedAt = now,
            UpdatedAt = now
        };
        copy.Versions.Add(new LabelTemplateVersion
        {
            VersionNumber = 1,
            Status = LabelTemplateVersionStatus.Draft,
            DesignJson = sourceVersion.DesignJson,
            Revision = 1,
            CreatedBy = CurrentUserName(),
            CreatedAt = now
        });

        db.LabelTemplates.Add(copy);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(GetTemplate), new { id = copy.Id }, MapDetail(copy));
    }

    /// <summary>Selecciona de forma explícita la plantilla que usarán todos los flujos operativos de este tipo.</summary>
    [HttpPost("{id:guid}/default")]
    public async Task<ActionResult<LabelTemplateDetailDto>> SetDefault(Guid id, CancellationToken cancellationToken)
    {
        var template = await LoadTemplateAsync(id, cancellationToken);
        if (template is null || template.IsArchived) return NotFound(new { message = "Plantilla no encontrada." });
        if (template.PublishedVersionId is null)
        {
            return Conflict(new { message = "Publica esta plantilla antes de elegirla como predeterminada." });
        }

        var currentDefaults = await db.LabelTemplates
            .Where(current => current.Kind == template.Kind && current.IsDefault && !current.IsArchived && current.Id != template.Id)
            .ToListAsync(cancellationToken);
        foreach (var current in currentDefaults) current.IsDefault = false;
        // El índice parcial exige quitar primero el predeterminado anterior; dos
        // escrituras evitan una colisión transitoria bajo PostgreSQL.
        if (currentDefaults.Count > 0) await db.SaveChangesAsync(cancellationToken);

        template.IsDefault = true;
        template.UpdatedAt = DateTime.UtcNow;
        template.UpdatedBy = CurrentUserName();
        await db.SaveChangesAsync(cancellationToken);
        return Ok(MapDetail(template));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> ArchiveTemplate(Guid id, CancellationToken cancellationToken)
    {
        var template = await db.LabelTemplates.FirstOrDefaultAsync(current => current.Id == id, cancellationToken);
        if (template is null) return NotFound(new { message = "Plantilla no encontrada." });
        if (template.IsArchived) return NoContent();

        var replacement = template.IsDefault
            ? await db.LabelTemplates
                .Where(current => current.Kind == template.Kind && current.Id != template.Id && !current.IsArchived && current.PublishedVersionId != null)
                .OrderByDescending(current => current.UpdatedAt)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        template.IsArchived = true;
        template.IsDefault = false;
        template.ArchivedAt = DateTime.UtcNow;
        template.UpdatedAt = DateTime.UtcNow;
        template.UpdatedBy = CurrentUserName();
        await db.SaveChangesAsync(cancellationToken);

        if (replacement is not null)
        {
            replacement.IsDefault = true;
            replacement.UpdatedAt = DateTime.UtcNow;
            replacement.UpdatedBy = CurrentUserName();
            await db.SaveChangesAsync(cancellationToken);
        }
        return NoContent();
    }

    [HttpGet("assets")]
    public async Task<ActionResult<List<LabelAssetDto>>> GetAssets(
        [FromQuery] bool includeArchived,
        CancellationToken cancellationToken)
    {
        var query = db.LabelAssets.AsNoTracking().AsQueryable();
        if (!includeArchived) query = query.Where(asset => !asset.IsArchived);

        var assets = await query
            .OrderByDescending(asset => asset.UploadedAt)
            .Select(asset => new LabelAssetDto(
                asset.Id,
                asset.Name,
                asset.OriginalFileName,
                asset.ContentType,
                asset.Url,
                asset.SizeBytes,
                asset.IsArchived,
                asset.UploadedBy,
                asset.UploadedAt))
            .ToListAsync(cancellationToken);
        return Ok(assets);
    }

    [HttpPost("assets")]
    [RequestSizeLimit(MaxAssetSizeBytes)]
    public async Task<ActionResult<LabelAssetDto>> UploadAsset(
        [FromForm] IFormFile file,
        [FromForm] string? name,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0) return BadRequest(new { message = "Selecciona una imagen para subir." });
        if (file.Length > MaxAssetSizeBytes) return BadRequest(new { message = "La imagen no puede exceder 5 MB." });

        await using var stream = file.OpenReadStream();
        var detectedContentType = await DetectImageContentTypeAsync(stream, cancellationToken);
        if (detectedContentType is null)
        {
            return BadRequest(new { message = "Sólo se permiten imágenes PNG, JPG o WebP válidas." });
        }

        stream.Position = 0;
        var safeFileName = Path.GetFileName(file.FileName);
        var extension = detectedContentType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            _ => ".img"
        };
        var assetName = NormalizeAssetName(name, safeFileName);
        var uploadFileName = $"{assetName}{extension}";
        var url = await cloudinaryService.UploadAsync(stream, uploadFileName, "label-assets");

        var asset = new LabelAsset
        {
            Name = assetName,
            OriginalFileName = safeFileName.Length > 260 ? safeFileName[..260] : safeFileName,
            ContentType = detectedContentType,
            Url = url,
            SizeBytes = file.Length,
            UploadedBy = CurrentUserName(),
            UploadedAt = DateTime.UtcNow
        };
        db.LabelAssets.Add(asset);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(GetAssets), new { }, MapAsset(asset));
    }

    [HttpPut("assets/{id:guid}")]
    public async Task<ActionResult<LabelAssetDto>> RenameAsset(
        Guid id,
        [FromBody] RenameLabelAssetDto request,
        CancellationToken cancellationToken)
    {
        var asset = await db.LabelAssets.FirstOrDefaultAsync(current => current.Id == id, cancellationToken);
        if (asset is null || asset.IsArchived) return NotFound(new { message = "Imagen no encontrada." });

        asset.Name = NormalizeAssetName(request.Name, asset.OriginalFileName);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(MapAsset(asset));
    }

    [HttpDelete("assets/{id:guid}")]
    public async Task<IActionResult> ArchiveAsset(Guid id, CancellationToken cancellationToken)
    {
        var asset = await db.LabelAssets.FirstOrDefaultAsync(current => current.Id == id, cancellationToken);
        if (asset is null) return NotFound(new { message = "Imagen no encontrada." });
        if (asset.IsArchived) return NoContent();

        var assetReference = asset.Id.ToString();
        var versionDesigns = await db.LabelTemplateVersions
            .AsNoTracking()
            .Select(version => version.DesignJson)
            .ToListAsync(cancellationToken);
        if (versionDesigns.Any(design => design.Contains(assetReference, StringComparison.OrdinalIgnoreCase)))
        {
            return Conflict(new { message = "Esta imagen se usa en una versión de etiqueta. Se conserva para no romper reimpresiones históricas." });
        }

        asset.IsArchived = true;
        asset.ArchivedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private async Task<ActionResult?> BuildValidationErrorAsync(
        LabelTemplateValidationResult validation,
        CancellationToken cancellationToken)
    {
        if (!validation.IsValid)
        {
            return BadRequest(new { message = "La plantilla tiene errores que impedirían imprimirla.", errors = validation.Errors });
        }

        if (validation.AssetIds.Count == 0) return null;

        var activeAssetIds = await db.LabelAssets
            .AsNoTracking()
            .Where(asset => validation.AssetIds.Contains(asset.Id) && !asset.IsArchived)
            .Select(asset => asset.Id)
            .ToListAsync(cancellationToken);
        var missingAssetIds = validation.AssetIds.Except(activeAssetIds).ToList();
        return missingAssetIds.Count == 0
            ? null
            : BadRequest(new
            {
                message = "La plantilla usa una imagen inexistente o archivada.",
                errors = missingAssetIds.Select(id => $"Activo no disponible: {id}").ToList()
            });
    }

    private async Task<LabelTemplate?> LoadTemplateAsync(Guid id, CancellationToken cancellationToken) =>
        await db.LabelTemplates
            .Include(template => template.PublishedVersion)
            .Include(template => template.Versions)
            .AsSplitQuery()
            .FirstOrDefaultAsync(template => template.Id == id, cancellationToken);

    private async Task<string> CreateCopyNameAsync(string sourceName, CancellationToken cancellationToken)
    {
        var baseName = sourceName.Length > 108 ? sourceName[..108] : sourceName;
        for (var sequence = 1; sequence <= 99; sequence++)
        {
            var suffix = sequence == 1 ? " (copia)" : $" (copia {sequence})";
            var candidate = $"{baseName[..Math.Min(baseName.Length, 120 - suffix.Length)]}{suffix}";
            var exists = await db.LabelTemplates.AnyAsync(template => !template.IsArchived && template.Name.ToLower() == candidate.ToLower(), cancellationToken);
            if (!exists) return candidate;
        }

        throw new InvalidOperationException("No fue posible generar un nombre disponible para la copia.");
    }

    private string CurrentUserName() =>
        User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "Administración";

    private static string? NormalizeNullable(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeAssetName(string? name, string fileName)
    {
        var candidate = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(fileName) : name.Trim();
        var normalized = new string(candidate.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? "Imagen sin nombre" : normalized[..Math.Min(normalized.Length, 120)];
    }

    private static async Task<string?> DetectImageContentTypeAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[16];
        var bytesRead = 0;
        while (bytesRead < header.Length)
        {
            var read = await stream.ReadAsync(header.AsMemory(bytesRead, header.Length - bytesRead), cancellationToken);
            if (read == 0) break;
            bytesRead += read;
        }

        if (bytesRead >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (bytesRead >= 3 && header[0] == 255 && header[1] == 216 && header[2] == 255) return "image/jpeg";
        if (bytesRead >= 12 && header.AsSpan(0, 4).SequenceEqual("RIFF"u8) && header.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }

    private static LabelTemplateDetailDto MapDetail(LabelTemplate template)
    {
        var versions = template.Versions.OrderByDescending(version => version.VersionNumber).Select(MapVersion).ToList();
        var draft = template.Versions.SingleOrDefault(version => version.Status == LabelTemplateVersionStatus.Draft);
        var published = template.PublishedVersion ?? template.Versions.SingleOrDefault(version => version.Id == template.PublishedVersionId);
        return new LabelTemplateDetailDto(
            template.Id,
            template.Name,
            template.Description,
            template.Kind.ToString(),
            template.PrinterProfile.ToString(),
            template.IsDefault,
            template.IsArchived,
            template.PublishedVersionId,
            draft is null ? null : MapVersion(draft),
            published is null ? null : MapVersion(published),
            versions,
            template.CreatedAt,
            template.UpdatedAt);
    }

    private static LabelTemplateVersionDto MapVersion(LabelTemplateVersion version) => new(
        version.Id,
        version.VersionNumber,
        version.Status.ToString(),
        version.DesignJson,
        version.Revision,
        version.CreatedBy,
        version.CreatedAt,
        version.PublishedBy,
        version.PublishedAt);

    private static LabelAssetDto MapAsset(LabelAsset asset) => new(
        asset.Id,
        asset.Name,
        asset.OriginalFileName,
        asset.ContentType,
        asset.Url,
        asset.SizeBytes,
        asset.IsArchived,
        asset.UploadedBy,
        asset.UploadedAt);
}
