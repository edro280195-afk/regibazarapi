using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models;

public class Delivery
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int OrderId { get; set; }

    [ForeignKey(nameof(OrderId))]
    public Order Order { get; set; } = null!;

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
