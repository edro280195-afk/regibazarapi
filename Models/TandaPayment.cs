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

    [Column("deposit_date")]
    public DateTime? DepositDate { get; set; }

    [Column("ocr_amount", TypeName = "decimal(12, 2)")]
    public decimal? OcrAmount { get; set; }

    [Column("proof_url")]
    [MaxLength(1000)]
    public string? ProofUrl { get; set; }

    [Column("ocr_text")]
    public string? OcrText { get; set; }

    [Column("ocr_confidence")]
    public decimal? OcrConfidence { get; set; }

    [Column("payment_method")]
    [MaxLength(50)]
    public string? PaymentMethod { get; set; }

    [Column("is_verified")]
    public bool IsVerified { get; set; } = false;

    [Column("notes")]
    public string? Notes { get; set; }

    // Relaciones
    [ForeignKey(nameof(ParticipantId))]
    [System.Text.Json.Serialization.JsonIgnore]
    public TandaParticipant? Participant { get; set; }
}
