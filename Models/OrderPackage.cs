using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models
{
    public class OrderPackage
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public int OrderId { get; set; }
        [ForeignKey(nameof(OrderId))]
        public Order Order { get; set; } = null!;

        public int PackageNumber { get; set; }

        [Required, MaxLength(100)]
        public string QrCodeValue { get; set; } = string.Empty;

        public PackageTrackingStatus Status { get; set; } = PackageTrackingStatus.Packed;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LoadedAt { get; set; }
        public DateTime? DeliveredAt { get; set; }
        public DateTime? ReturnedAt { get; set; }
    }

    public enum PackageTrackingStatus
    {
        Packed = 0,
        Loaded = 1,
        Delivered = 2,
        Returned = 3
    }
}
