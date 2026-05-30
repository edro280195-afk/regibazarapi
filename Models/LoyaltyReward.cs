using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models;

public enum LoyaltyRewardType
{
    FixedDiscount = 0, // Descuento de monto fijo (Value en MXN)
    FreeShipping = 1,  // Cubre el costo de envío del pedido
    Gift = 2           // Regalo físico, sin descuento monetario
}

/// <summary>
/// Catálogo de premios que las clientas pueden canjear con sus RegiPuntos.
/// Editable: la dueña puede activar/desactivar o ajustar costos sin tocar código.
/// </summary>
public class LoyaltyReward
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(300)]
    public string? Description { get; set; }

    /// <summary>Cuántos RegiPuntos cuesta canjear este premio.</summary>
    public int PointsCost { get; set; }

    public LoyaltyRewardType Type { get; set; } = LoyaltyRewardType.FixedDiscount;

    /// <summary>Monto del descuento en MXN (solo para FixedDiscount; 0 en los demás).</summary>
    [Column(TypeName = "decimal(10,2)")]
    public decimal Value { get; set; }

    [MaxLength(16)]
    public string? Icon { get; set; }

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; } = 0;
}
