using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models;

[Table("tanda_participants")]
public class TandaParticipant
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    // Relaciones
    [ForeignKey(nameof(Client))]
    [Column("customer_id")]
    public int CustomerId { get; set; } 

    [ForeignKey(nameof(Tanda))]
    [Column("tanda_id")]
    public Guid TandaId { get; set; }

    [Column("assigned_turn")]
    public int AssignedTurn { get; set; }

    [Column("is_delivered")]
    public bool IsDelivered { get; set; } = false;

    [Column("delivery_date", TypeName = "date")]
    public DateTime? DeliveryDate { get; set; }

    [Column("status")]
    [MaxLength(50)]
    public string Status { get; set; } = "Active"; // Active, Delinquent, Completed

    [Column("variant")]
    [MaxLength(255)]
    public string? Variant { get; set; }

    [NotMapped]
    public string? CustomerName { get; set; }

    // Relaciones
    public virtual Client? Client { get; set; }
    
    [System.Text.Json.Serialization.JsonIgnore]
    public Tanda? Tanda { get; set; }
    
    public ICollection<TandaPayment> Payments { get; set; } = new List<TandaPayment>();
}
