using System.ComponentModel.DataAnnotations;

namespace EntregasApi.Models;

public class SalesPeriod
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public DateTime StartDate { get; set; }

    public DateTime EndDate { get; set; }

    public bool IsActive { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public ICollection<Order> Orders { get; set; } = new List<Order>();
    public ICollection<Investment> Investments { get; set; } = new List<Investment>();
}
