using EntregasApi.DTOs;
using System.ComponentModel;
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

    public int? SalesPeriodId { get; set; }
    [ForeignKey(nameof(SalesPeriodId))]
    public virtual SalesPeriod? SalesPeriod { get; set; }

    [Column(TypeName = "decimal(10,2)")]
    public decimal Subtotal { get; set; }

    [Column(TypeName = "decimal(10,2)")]
    public decimal ShippingCost { get; set; } = 60m;

    [Column(TypeName = "decimal(10,2)")]
    public decimal Total { get; set; }

    [Column(TypeName = "decimal(10,2)")]
    public decimal DiscountAmount { get; set; } = 0m;

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
    public string? DeliveryInstructions { get; set; }

    public int? TotalPackages { get; set; }
    public bool IsFullyPacked { get; set; }
    public bool IsFullyLoaded { get; set; }

    public string? AlternativeAddress { get; set; }

    [Obsolete("Usar Payments collection")]
    [Column(TypeName = "decimal(10,2)")]
    public decimal AdvancePayment { get; set; } = 0m;

    [Obsolete("Usar Payments collection")]
    public string? PaymentMethod { get; set; }

    public ICollection<OrderPayment> Payments { get; set; } = new List<OrderPayment>();
    public ICollection<OrderPackage> Packages { get; set; } = new List<OrderPackage>();

    [NotMapped]
    public decimal AmountPaid => (Payments?.Sum(p => p.Amount) ?? 0m) + AdvancePayment;

    [NotMapped]
    public decimal BalanceDue => Total - AmountPaid;
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

    public int? ProductId { get; set; }
    [ForeignKey(nameof(ProductId))]
    public Product? Product { get; set; }

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

public enum OrderType
{
    Delivery = 0,
    PickUp = 1,
    POS_Tienda = 2
}