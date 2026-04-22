using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models
{
    public class ChatMessage
    {
        public int Id { get; set; }

        public int? DeliveryRouteId { get; set; }

        [Required]
        public string Sender { get; set; } = "Admin"; // "Admin", "Driver", "Client"

        [Required]
        public string Text { get; set; } = string.Empty;

        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        public int? DeliveryId { get; set; }

        // Relaciones
        [ForeignKey(nameof(DeliveryRouteId))]
        public virtual DeliveryRoute? DeliveryRoute { get; set; }

        [ForeignKey(nameof(DeliveryId))]
        public virtual Delivery? Delivery { get; set; }
    }
}
