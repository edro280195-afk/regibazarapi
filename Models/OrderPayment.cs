using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models;

public class OrderPayment
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int OrderId { get; set; }
    [ForeignKey(nameof(OrderId))]
    public Order Order { get; set; } = null!;

    [Column(TypeName = "decimal(10,2)")]
    public decimal Amount { get; set; }

    [Required, MaxLength(50)]
    public string Method { get; set; } = "Efectivo"; // Efectivo, Transferencia, Deposito, Tarjeta

    public DateTime Date { get; set; } = DateTime.UtcNow;

    [Required, MaxLength(20)]
    public string RegisteredBy { get; set; } = "Admin"; // Admin | Driver

    [MaxLength(500)]
    public string? Notes { get; set; }
}
