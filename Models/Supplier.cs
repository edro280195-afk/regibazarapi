using System.ComponentModel.DataAnnotations;

namespace EntregasApi.Models
{
    public class Supplier
    {
        public int Id { get; set; }

        [Required, MaxLength(200)]
        public string Name { get; set; } = string.Empty;

        [MaxLength(200)]
        public string? ContactName { get; set; }

        [MaxLength(50)]
        public string? Phone { get; set; }

        [MaxLength(500)]
        public string? Notes { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Navigation
        public List<Investment> Investments { get; set; } = new();

        public string Currency { get; set; } = "MXN"; // Guardamos "MXN" o "USD"
        public decimal ExchangeRate { get; set; } = 1.0m;
    }
}
