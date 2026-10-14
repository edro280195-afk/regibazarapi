using System.ComponentModel.DataAnnotations;

namespace EntregasApi.Models;

/// <summary>
/// Agrupa pedidos existentes en una sola bolsa sin mover ni combinar sus artículos o pagos.
/// </summary>
public class OrderConsolidation
{
    [Key]
    public int Id { get; set; }

    /// <summary>Pedido de la clienta que define el destino de la bolsa.</summary>
    public int PrimaryOrderId { get; set; }

    /// <summary>Identificadores y datos congelados al crear el grupo para conservar la trazabilidad.</summary>
    public int PrimaryClientId { get; set; }

    [Required, MaxLength(200)]
    public string PrimaryClientNameSnapshot { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string PrimaryAddressSnapshot { get; set; } = string.Empty;

    public double? PrimaryLatitude { get; set; }
    public double? PrimaryLongitude { get; set; }

    [MaxLength(1000)]
    public string? DeliveryInstructionsSnapshot { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<Order> Orders { get; set; } = new List<Order>();
}
