using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models;

[Table("payments")]
public class TandaPayment
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("participant_id")]
    public Guid ParticipantId { get; set; }

    [Column("week_number")]
    public int WeekNumber { get; set; }

    [Column("amount_paid", TypeName = "decimal(12, 2)")]
    public decimal AmountPaid { get; set; }

    [Column("penalty_paid", TypeName = "decimal(12, 2)")]
    public decimal PenaltyPaid { get; set; } = 0;

    [Column("payment_date")]
    public DateTime PaymentDate { get; set; } 

    [Column("is_verified")]
    public bool IsVerified { get; set; } = false;

    [Column("notes")]
    public string? Notes { get; set; }

    // Relaciones
    [ForeignKey(nameof(ParticipantId))]
    [System.Text.Json.Serialization.JsonIgnore]
    public TandaParticipant? Participant { get; set; }
}
