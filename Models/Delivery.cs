using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models;

public class Delivery
{
    [Key]
    public int Id { get; set; }

    // Una Delivery apunta a una Order normal O a un TandaParticipant (XOR, ver AppDbContext).
    public int? OrderId { get; set; }

    [ForeignKey(nameof(OrderId))]
    public Order? Order { get; set; }

    public Guid? TandaParticipantId { get; set; }

    [ForeignKey(nameof(TandaParticipantId))]
    public TandaParticipant? TandaParticipant { get; set; }

    /// <summary>Tipo de entrega: pedido regular o turno de tanda.</summary>
    public DeliveryKind Kind { get; set; } = DeliveryKind.Order;

    [Required]
    public int DeliveryRouteId { get; set; }

    [ForeignKey(nameof(DeliveryRouteId))]
    public DeliveryRoute DeliveryRoute { get; set; } = null!;

    /// <summary>Posición en la ruta (orden de entrega)</summary>
    public int SortOrder { get; set; }

    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;

    [MaxLength(500)]
    public string? Notes { get; set; }

    /// <summary>Motivo de no entrega</summary>
    [MaxLength(500)]
    public string? FailureReason { get; set; }

    public DateTime? DeliveredAt { get; set; }

    /// <summary>Firma digital de quien recibe (SVG inline del canvas de firma).</summary>
    public string? SignatureSvg { get; set; }

    /// <summary>Nombre de quien firmó el pedido al momento de la entrega.</summary>
    [MaxLength(120)]
    public string? SignedByName { get; set; }

    /// <summary>Fecha/hora en que se capturó la firma.</summary>
    public DateTime? SignedAt { get; set; }

    /// <summary>Momento en que el repartidor llegó cerca del destino (proximidad 300m)</summary>
    public DateTime? ArrivedAt { get; set; }

    public ICollection<DeliveryEvidence> Evidences { get; set; } = new List<DeliveryEvidence>();
}

public class DeliveryEvidence
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int DeliveryId { get; set; }

    [ForeignKey(nameof(DeliveryId))]
    public Delivery Delivery { get; set; } = null!;

    /// <summary>Ruta al archivo de imagen</summary>
    [Required, MaxLength(500)]
    public string ImagePath { get; set; } = string.Empty;

    public EvidenceType Type { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum DeliveryStatus
{
    Pending = 0,
    Delivered = 1,
    NotDelivered = 2,
    InTransit = 3
}

public enum EvidenceType
{
    DeliveryProof = 0,
    NonDeliveryProof = 1
}

public enum DeliveryKind
{
    Order = 0,
    Tanda = 1
}
