using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models;

[Table("tandas")]
public class Tanda
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("product_id")]
    public Guid ProductId { get; set; }

    [Required]
    [MaxLength(255)]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [Column("total_weeks")]
    public int TotalWeeks { get; set; }

    [Column("weekly_amount", TypeName = "decimal(12, 2)")]
    public decimal WeeklyAmount { get; set; }

    [Column("penalty_amount", TypeName = "decimal(12, 2)")]
    public decimal PenaltyAmount { get; set; } = 0;

    [Column("start_date", TypeName = "date")]
    public DateTime StartDate { get; set; }

    [Column("status")]
    [MaxLength(50)]
    public string Status { get; set; } = "Draft"; // Draft, Active, Completed, Cancelled

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Required, MaxLength(64)]
    [Column("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    // Relaciones
    [ForeignKey(nameof(ProductId))]
    public TandaProduct? Product { get; set; }
    public ICollection<TandaParticipant> Participants { get; set; } = new List<TandaParticipant>();
}
