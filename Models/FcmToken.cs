using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models;

[Table("FcmTokens")]
public class FcmToken
{
    [Key]
    public int Id { get; set; }

    /// <summary>Token FCM único del dispositivo Android</summary>
    [Required]
    [MaxLength(512)]
    public string Token { get; set; } = string.Empty;

    /// <summary>Rol del dispositivo: "driver" | "admin"</summary>
    [Required]
    [MaxLength(20)]
    public string Role { get; set; } = "driver";

    /// <summary>Token de ruta activa del chofer (se actualiza al entrar a una ruta)</summary>
    [MaxLength(255)]
    public string? DriverRouteToken { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
