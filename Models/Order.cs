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

    /// <summary>Subtotal de artículos</summary>
    [Column(TypeName = "decimal(10,2)")]
    public decimal Subtotal { get; set; }

    /// <summary>Costo de envío (default $60 MXN)</summary>
    [Column(TypeName = "decimal(10,2)")]
    public decimal ShippingCost { get; set; } = 60m;

    [Column(TypeName = "decimal(10,2)")]
    public decimal Total { get; set; }

    /// <summary>Token único para el enlace de la clienta</summary>
    [Required, MaxLength(64)]
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>Expiración del enlace</summary>
    public DateTime ExpiresAt { get; set; }

    public OrderStatus Status { get; set; } = OrderStatus.Pending;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<OrderItem> Items { get; set; } = new List<OrderItem>();
    public Delivery? Delivery { get; set; }

    public OrderType OrderType { get; set; } = OrderType.Delivery;

    public DateTime? PostponedAt { get; set; } // Fecha y hora elegida
    public string? PostponedNote { get; set; } // "Nota de cuando"
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
    Posponed = 5
}
