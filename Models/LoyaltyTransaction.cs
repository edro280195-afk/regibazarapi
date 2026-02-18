using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models
{
    public class LoyaltyTransaction
    {
        public int Id { get; set; }

        public int ClientId { get; set; }

        public int Points { get; set; } // Positivo (gana) o Negativo (gasta)

        [Required, MaxLength(200)]
        public string Reason { get; set; } = string.Empty; // "Compra #123", "Regalo Admin"

        public DateTime Date { get; set; } = DateTime.UtcNow;

        // Relación
        [ForeignKey(nameof(ClientId))]
        public Client Client { get; set; } = null!;
    }
}
