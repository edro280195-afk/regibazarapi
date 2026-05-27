using EntregasApi.DTOs;
using System.ComponentModel.DataAnnotations;

namespace EntregasApi.Models;

public class Client
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(20)]
    public string? Phone { get; set; }

    [MaxLength(500)]
    public string? Address { get; set; }

    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Type { get; set; } = "Nueva";
    public ClientTag Tag { get; set; } = ClientTag.None;
    public int CurrentPoints { get; set; } = 0;
    public int LifetimePoints { get; set; } = 0;
    public string? DeliveryInstructions { get; set; }

    // Identidad normalizada para fuzzy matching y resolución multi-señal
    public string NormalizedName { get; set; } = string.Empty;
    public string? NormalizedPhone { get; set; }
    public string? NormalizedAddress { get; set; }

    public ICollection<Order> Orders { get; set; } = new List<Order>();
    public ICollection<ClientAlias> Aliases { get; set; } = new List<ClientAlias>();
}
