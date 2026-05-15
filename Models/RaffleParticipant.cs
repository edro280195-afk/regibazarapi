using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models;

[Table("raffle_participants")]
public class RaffleParticipant
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Required]
    [Column("raffle_id")]
    public Guid RaffleId { get; set; }

    [Required]
    [Column("client_id")]
    public int ClientId { get; set; }

    [Column("qualification_date")]
    public DateTime QualificationDate { get; set; } = DateTime.UtcNow;

    [Column("qualifying_orders")]
    public int QualifyingOrders { get; set; }

    [Column("qualifying_total_spent", TypeName = "decimal(12, 2)")]
    public decimal? QualifyingTotalSpent { get; set; }

    [Column("entry_count")]
    public int EntryCount { get; set; } = 1;

    [Column("is_winner")]
    public bool IsWinner { get; set; }

    // Para sorteos de tanda: el nuevo turno asignado
    [Column("assigned_tanda_turn")]
    public int? AssignedTandaTurn { get; set; }

    // Para sorteos de tanda: el turno anterior (para referencia)
    [Column("previous_tanda_turn")]
    public int? PreviousTandaTurn { get; set; }

    [Column("notified")]
    public bool Notified { get; set; }

    [Column("notified_at")]
    public DateTime? NotifiedAt { get; set; }

    [Column("notification_channel_used")]
    public string? NotificationChannelUsed { get; set; }

    // Relaciones
    [ForeignKey(nameof(RaffleId))]
    public Raffle Raffle { get; set; } = null!;

    [ForeignKey(nameof(ClientId))]
    public Client Client { get; set; } = null!;
}
