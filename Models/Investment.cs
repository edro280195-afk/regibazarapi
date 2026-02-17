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
    }
}
