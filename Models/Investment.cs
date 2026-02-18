using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models
{
    public class Investment
    {
        public int Id { get; set; }

        public int SupplierId { get; set; }

        [Column(TypeName = "decimal(12,2)")]
        public decimal Amount { get; set; }

        public DateTime Date { get; set; }

        [MaxLength(500)]
        public string? Notes { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Navigation
        [ForeignKey(nameof(SupplierId))]
        public Supplier Supplier { get; set; } = null!;

        // ---------------------------------------------------------
        [MaxLength(10)]
        public string Currency { get; set; } = "MXN"; // Por defecto Pesos

        [Column(TypeName = "decimal(18,4)")] // 4 decimales para mayor precisión en el tipo de cambio
        public decimal ExchangeRate { get; set; } = 1.0m; // Por defecto 1
        // ---------------------------------------------------------


    }
}
