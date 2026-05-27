using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models;

[Table("raffle_draws")]
public class RaffleDraw
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Required]
    [Column("raffle_id")]
    public Guid RaffleId { get; set; }

    [Column("draw_date")]
    public DateTime DrawDate { get; set; } = DateTime.UtcNow;

    [Column("winner_id")]
    public int? WinnerId { get; set; }

    [Column("selection_method")]
    [MaxLength(50)]
    public string SelectionMethod { get; set; } = "random"; // random, manual, tandaShuffle

    [Column("is_tanda_shuffle")]
    public bool IsTandaShuffle { get; set; }

    [Column("tanda_turns_reshuffled")]
    public int? TandaTurnsReshuffled { get; set; }

    [MaxLength(1000)]
    [Column("notes")]
    public string? Notes { get; set; }

    // Relaciones
    [ForeignKey(nameof(RaffleId))]
    public Raffle Raffle { get; set; } = null!;

    [ForeignKey(nameof(WinnerId))]
    public Client? Winner { get; set; }
}
