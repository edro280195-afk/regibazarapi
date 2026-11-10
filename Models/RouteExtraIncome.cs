using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models;

[Table("route_extra_incomes")]
public class RouteExtraIncome
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [Column("delivery_route_id")]
    public int DeliveryRouteId { get; set; }

    [Column("delivery_id")]
    public int? DeliveryId { get; set; }

    [Column("amount", TypeName = "decimal(12, 2)")]
    public decimal Amount { get; set; }

    [Required, MaxLength(50)]
    [Column("payment_method")]
    public string PaymentMethod { get; set; } = "Efectivo"; // Efectivo / Transferencia

    [Required, MaxLength(50)]
    [Column("income_type")]
    public string IncomeType { get; set; } = "Tanda"; // Tanda / Otro

    [MaxLength(200)]
    [Column("client_name")]
    public string? ClientName { get; set; }

    [MaxLength(500)]
    [Column("notes")]
    public string? Notes { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Relaciones
    [ForeignKey(nameof(DeliveryRouteId))]
    public DeliveryRoute? DeliveryRoute { get; set; }
}
