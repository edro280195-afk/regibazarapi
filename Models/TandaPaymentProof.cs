using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models;

[Table("tanda_payment_proofs")]
public class TandaPaymentProof
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("participant_id")]
    public Guid ParticipantId { get; set; }

    [Column("tanda_id")]
    public Guid TandaId { get; set; }

    [Column("week_number")]
    public int WeekNumber { get; set; }

    [Column("amount_claimed", TypeName = "decimal(12, 2)")]
    public decimal AmountClaimed { get; set; }

    [Column("deposit_date")]
    public DateTime? DepositDate { get; set; }

    [Column("ocr_amount", TypeName = "decimal(12, 2)")]
    public decimal? OcrAmount { get; set; }

    [Column("ocr_text")]
    public string? OcrText { get; set; }

    [Column("ocr_confidence")]
    public decimal? OcrConfidence { get; set; }

    [Required, MaxLength(500)]
    [Column("file_url")]
    public string FileUrl { get; set; } = string.Empty;

    [Required, MaxLength(50)]
    [Column("file_type")]
    public string FileType { get; set; } = string.Empty;

    [Required, MaxLength(20)]
    [Column("status")]
    public string Status { get; set; } = "Pending";

    [Column("submitted_at")]
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;

    [Column("reviewed_at")]
    public DateTime? ReviewedAt { get; set; }

    [MaxLength(255)]
    [Column("reviewed_by")]
    public string? ReviewedBy { get; set; }

    [MaxLength(500)]
    [Column("rejection_reason")]
    public string? RejectionReason { get; set; }

    [Column("registered_payment_id")]
    public Guid? RegisteredPaymentId { get; set; }

    [ForeignKey(nameof(ParticipantId))]
    public TandaParticipant? Participant { get; set; }

    [ForeignKey(nameof(TandaId))]
    public Tanda? Tanda { get; set; }

    [ForeignKey(nameof(RegisteredPaymentId))]
    public TandaPayment? RegisteredPayment { get; set; }
}
