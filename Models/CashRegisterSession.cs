using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models;

public class CashRegisterSession
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int UserId { get; set; }
    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;

    public DateTime OpeningTime { get; set; } = DateTime.UtcNow;
    
    public DateTime? ClosingTime { get; set; }

    [Column(TypeName = "decimal(12,2)")]
    public decimal InitialCash { get; set; }

    [Column(TypeName = "decimal(12,2)")]
    public decimal FinalCashExpected { get; set; }

    [Column(TypeName = "decimal(12,2)")]
    public decimal? FinalCashActual { get; set; }

    public SessionStatus Status { get; set; } = SessionStatus.Open;

    public ICollection<OrderPayment> Payments { get; set; } = new List<OrderPayment>();
}

public enum SessionStatus
{
    Open = 0,
    Closed = 1
}
