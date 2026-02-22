using EntregasApi.DTOs;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models;

public class Order
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int ClientId { get; set; }
    [ForeignKey(nameof(ClientId))]
    public Client Client { get; set; } = null!;

    public int? DeliveryRouteId { get; set; }
    [ForeignKey(nameof(DeliveryRouteId))]
    public DeliveryRoute? DeliveryRoute { get; set; }

    [Column(TypeName = "decimal(10,2)")]
    public decimal Subtotal { get; set; }

    [Column(TypeName = "decimal(10,2)")]
    public decimal ShippingCost { get; set; } = 60m;

    [Column(TypeName = "decimal(10,2)")]
    public decimal Total { get; set; }

    [Required, MaxLength(64)]
    public string AccessToken { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<OrderItem> Items { get; set; } = new List<OrderItem>();
    public Delivery? Delivery { get; set; }
    public OrderType OrderType { get; set; } = OrderType.Delivery;
    public DateTime? PostponedAt { get; set; }
    public string? PostponedNote { get; set; }

    // ── Nuevos campos ──
    public string? Tags { get; set; }
    public string? DeliveryTime { get; set; }
    public string? PickupDate { get; set; }

    [Column(TypeName = "decimal(10,2)")]
    public decimal AdvancePayment { get; set; } = 0m;
}

public class OrderItem
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int OrderId { get; set; }
    [ForeignKey(nameof(OrderId))]
    public Order Order { get; set; } = null!;

    [Required, MaxLength(300)]
    public string ProductName { get; set; } = string.Empty;

    public int Quantity { get; set; } = 1;

    [Column(TypeName = "decimal(10,2)")]
    public decimal UnitPrice { get; set; }

    [Column(TypeName = "decimal(10,2)")]
    public decimal LineTotal { get; set; }
}

public enum OrderStatus
{
    Pending = 0,
    InRoute = 1,
    Delivered = 2,
    NotDelivered = 3,
    Canceled = 4,
    Postponed = 5,
    Confirmed = 6,
    Shipped = 7
}