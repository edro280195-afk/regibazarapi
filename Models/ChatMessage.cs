using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models
{
    public class ChatMessage
    {
        public int Id { get; set; }

        public int DeliveryRouteId { get; set; }

        [Required]
        public string Sender { get; set; } = "Admin"; // "Admin" o "Driver"

        [Required]
        public string Text { get; set; } = string.Empty;

        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        // Relación
        [ForeignKey(nameof(DeliveryRouteId))]
        public DeliveryRoute DeliveryRoute { get; set; } = null!;
    }
}
