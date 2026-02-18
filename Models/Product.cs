using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models
{
    public class Product
    {
        public int Id { get; set; }

        [Required, MaxLength(100)]
        public string Name { get; set; } = string.Empty;

        [Column(TypeName = "decimal(12,2)")]
        public decimal DefaultPrice { get; set; }

        public string? Category { get; set; }

        public int Stock { get; set; }

        public bool IsActive { get; set; } = true;
    }
}
