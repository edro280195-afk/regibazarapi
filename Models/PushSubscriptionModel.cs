using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models;

[Table("PushSubscriptions")]
public class PushSubscriptionModel
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(2048)]
    public string Endpoint { get; set; } = string.Empty;

    [Required]
    [MaxLength(512)]
    public string P256dh { get; set; } = string.Empty;

    [Required]
    [MaxLength(512)]
    public string Auth { get; set; } = string.Empty;

    /// <summary>
    /// Rol del suscriptor: "client", "driver", "admin"
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string Role { get; set; } = "client";

    /// <summary>
    /// ID de la clienta (solo cuando Role = "client")
    /// </summary>
    public int? ClientId { get; set; }

    /// <summary>
    /// Token de ruta del chofer (solo cuando Role = "driver")
    /// </summary>
    [MaxLength(255)]
    public string? DriverRouteToken { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastUsedAt { get; set; }
}