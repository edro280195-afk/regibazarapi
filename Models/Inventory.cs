using System.ComponentModel.DataAnnotations;

namespace EntregasApi.Models;

/// <summary>
/// Caja física de mercancía. La etiqueta NFC solamente apunta a esta entidad;
/// el contenido se conserva siempre en la base de datos.
/// </summary>
public class InventoryBox
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(30)]
    public string Code { get; set; } = string.Empty;

    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Location { get; set; }

    /// <summary>Token aleatorio que se escribe dentro de la URL NDEF.</summary>
    [Required, MaxLength(64)]
    public string NfcToken { get; set; } = string.Empty;

    /// <summary>UID técnico del tag; se usa para detectar una etiqueta reasignada.</summary>
    [MaxLength(64)]
    public string? NfcTagUid { get; set; }

    public bool IsNfcBound { get; set; }
    public bool IsArchived { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<InventoryItem> Items { get; set; } = new List<InventoryItem>();
    public ICollection<InventoryMovement> Movements { get; set; } = new List<InventoryMovement>();
    public ICollection<InventoryCountSession> CountSessions { get; set; } = new List<InventoryCountSession>();
}

/// <summary>Existencia actual de un artículo dentro de una caja.</summary>
public class InventoryItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InventoryBoxId { get; set; }
    public InventoryBox InventoryBox { get; set; } = null!;

    [Required, MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(120)]
    public string? Variant { get; set; }

    [MaxLength(100)]
    public string? Barcode { get; set; }

    /// <summary>
    /// Código interno permanente para etiquetar el artículo, aun cuando el producto
    /// no tenga código comercial. Es único por renglón físico de inventario.
    /// </summary>
    [Required, MaxLength(40)]
    public string LabelCode { get; set; } = string.Empty;

    public int Quantity { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<InventoryMovement> Movements { get; set; } = new List<InventoryMovement>();
}

public enum InventoryMovementType
{
    InitialCount = 0,
    Added = 1,
    Removed = 2,
    Adjusted = 3,
    TransferOut = 4,
    TransferIn = 5,
    CountAdjustment = 6
}

/// <summary>Bitácora inmutable de cada cambio al inventario físico.</summary>
public class InventoryMovement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InventoryBoxId { get; set; }
    public InventoryBox InventoryBox { get; set; } = null!;
    public Guid? InventoryItemId { get; set; }
    public InventoryItem? InventoryItem { get; set; }
    public Guid? TransferGroupId { get; set; }
    public InventoryMovementType Type { get; set; }
    public int QuantityDelta { get; set; }
    public int QuantityAfter { get; set; }

    [MaxLength(300)]
    public string? Note { get; set; }

    [Required, MaxLength(120)]
    public string PerformedBy { get; set; } = "Sistema";

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Corte de inventario de una caja. Conserva la foto del conteo aunque la existencia cambie despuÃ©s.
/// </summary>
public class InventoryCountSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InventoryBoxId { get; set; }
    public InventoryBox InventoryBox { get; set; } = null!;

    [MaxLength(300)]
    public string? Note { get; set; }

    [Required, MaxLength(120)]
    public string PerformedBy { get; set; } = "Sistema";

    public DateTime CountedAt { get; set; } = DateTime.UtcNow;
    public ICollection<InventoryCountEntry> Entries { get; set; } = new List<InventoryCountEntry>();
}

/// <summary>RenglÃ³n inmutable de una sesiÃ³n de conteo fÃ­sico.</summary>
public class InventoryCountEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InventoryCountSessionId { get; set; }
    public InventoryCountSession InventoryCountSession { get; set; } = null!;
    public Guid InventoryItemId { get; set; }

    [Required, MaxLength(150)]
    public string ItemName { get; set; } = string.Empty;

    [MaxLength(120)]
    public string? Variant { get; set; }

    public int ExpectedQuantity { get; set; }
    public int ActualQuantity { get; set; }
    public int Difference { get; set; }
}
