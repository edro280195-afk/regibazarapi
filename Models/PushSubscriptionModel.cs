using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models;

public class PushSubscriptionModel
{
    [Key]
    public int Id { get; set; }

    [Required]
    public string Endpoint { get; set; } = string.Empty;

    [Required]
    public string P256dh { get; set; } = string.Empty;

    [Required]
    public string Auth { get; set; } = string.Empty;

    // Optional user mapping. 
    public int? UserId { get; set; }

    // Optional client mapping
    public int? ClientId { get; set; }
    
    [ForeignKey("ClientId")]
    public Client? Client { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
