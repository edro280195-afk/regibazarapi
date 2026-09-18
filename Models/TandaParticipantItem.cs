using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models;

[Table("tanda_participant_items")]
public class TandaParticipantItem
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("participant_id")]
    public Guid ParticipantId { get; set; }

    [Column("product_id")]
    public Guid? ProductId { get; set; }

    [Required, MaxLength(255)]
    [Column("product_name")]
    public string ProductName { get; set; } = string.Empty;

    [Column("quantity")]
    public int Quantity { get; set; } = 1;

    [Column("unit_price", TypeName = "decimal(12, 2)")]
    public decimal UnitPrice { get; set; }

    [Column("weekly_amount", TypeName = "decimal(12, 2)")]
    public decimal? WeeklyAmount { get; set; }

    [Column("variant")]
    [MaxLength(255)]
    public string? Variant { get; set; }

    [ForeignKey(nameof(ParticipantId))]
    [System.Text.Json.Serialization.JsonIgnore]
    public TandaParticipant? Participant { get; set; }

    [ForeignKey(nameof(ProductId))]
    public TandaProduct? Product { get; set; }

    [NotMapped]
    public decimal LineTotal => Quantity * UnitPrice;
}
