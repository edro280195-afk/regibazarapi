using System.ComponentModel.DataAnnotations;

namespace EntregasApi.Models;

/// <summary>Destino de negocio al que se puede asociar una etiqueta.</summary>
public enum LabelTemplateKind
{
    InventoryBox = 0,
    InventoryItem = 1,
    OrderPackage = 2
}

/// <summary>Medida física y densidad de impresión esperada para una plantilla.</summary>
public enum LabelPrinterProfile
{
    NiimbotB1_50x50 = 0,
    AiyinE40_4x6 = 1
}

public enum LabelTemplateVersionStatus
{
    Draft = 0,
    Published = 1,
    Archived = 2
}

public enum LabelPrintMethod
{
    BrowserPrint = 0,
    BrowserBluetooth = 1,
    NativeBluetooth = 2,
    SystemShare = 3,
    PdfExport = 4,
    TestPrint = 5,
    NativeSystemPrint = 6
}

/// <summary>
/// Definición estable de una familia de etiquetas. La versión publicada se conserva
/// inmutable para que una reimpresión posterior sea idéntica a la original.
/// </summary>
public class LabelTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(400)]
    public string? Description { get; set; }

    public LabelTemplateKind Kind { get; set; }
    public LabelPrinterProfile PrinterProfile { get; set; }
    /// <summary>Una sola plantilla activa por tipo se usa automáticamente al imprimir.</summary>
    public bool IsDefault { get; set; }
    public bool IsArchived { get; set; }

    public Guid? PublishedVersionId { get; set; }
    public LabelTemplateVersion? PublishedVersion { get; set; }

    [Required, MaxLength(120)]
    public string CreatedBy { get; set; } = "Sistema";

    [MaxLength(120)]
    public string? UpdatedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ArchivedAt { get; set; }

    public ICollection<LabelTemplateVersion> Versions { get; set; } = new List<LabelTemplateVersion>();
}

/// <summary>
/// Instantánea editable o publicada del diseño. DesignJson usa milímetros y el
/// esquema versionado del diseñador; nunca contiene HTML ejecutable.
/// </summary>
public class LabelTemplateVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LabelTemplateId { get; set; }
    public LabelTemplate LabelTemplate { get; set; } = null!;

    public int VersionNumber { get; set; }
    public LabelTemplateVersionStatus Status { get; set; } = LabelTemplateVersionStatus.Draft;

    [Required]
    public string DesignJson { get; set; } = string.Empty;

    /// <summary>Control optimista de concurrencia del borrador.</summary>
    public int Revision { get; set; } = 1;

    [MaxLength(120)]
    public string CreatedBy { get; set; } = "Sistema";

    [MaxLength(120)]
    public string? PublishedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PublishedAt { get; set; }
}

/// <summary>Imagen administrada por el sistema y disponible para usarse en plantillas.</summary>
public class LabelAsset
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [Required, MaxLength(260)]
    public string OriginalFileName { get; set; } = string.Empty;

    [Required, MaxLength(120)]
    public string ContentType { get; set; } = string.Empty;

    [Required, MaxLength(1200)]
    public string Url { get; set; } = string.Empty;

    public long SizeBytes { get; set; }
    public bool IsArchived { get; set; }

    [Required, MaxLength(120)]
    public string UploadedBy { get; set; } = "Sistema";

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ArchivedAt { get; set; }
}

/// <summary>
/// Bitácora de solicitudes de impresión. Registra la intención de imprimir; el
/// hardware no puede garantizar que la impresión física haya terminado.
/// </summary>
public class LabelPrintEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid LabelTemplateVersionId { get; set; }
    public LabelTemplateVersion LabelTemplateVersion { get; set; } = null!;

    public LabelTemplateKind TargetKind { get; set; }

    [Required, MaxLength(64)]
    public string TargetId { get; set; } = string.Empty;

    public LabelPrinterProfile PrinterProfile { get; set; }
    public LabelPrintMethod Method { get; set; }

    [Range(1, 100)]
    public int Copies { get; set; } = 1;

    [Required, MaxLength(120)]
    public string RequestedBy { get; set; } = "Sistema";

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
}
