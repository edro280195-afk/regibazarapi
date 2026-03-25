using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models
{
    // Hacemos que la búsqueda por código QR (SKU) sea ultrarrápida y no se repita
    [Index(nameof(SKU), IsUnique = true)]
    public class Product
    {
        public int Id { get; set; }
        [Required, MaxLength(50)]
        public string SKU { get; set; } = string.Empty; // Código QR
        [Required, MaxLength(100)]
        public string Name { get; set; } = string.Empty;
        // Se fue ImageUrl 🗑️
        [Column(TypeName = "decimal(12,2)")]
        public decimal Price { get; set; } // Precio de venta en tienda
        public int Stock { get; set; }
        public bool IsActive { get; set; } = true;
    }
}