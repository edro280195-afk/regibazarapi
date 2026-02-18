using System.ComponentModel.DataAnnotations;

namespace EntregasApi.Models;

public class DeliveryRoute
{
    [Key]
    public int Id { get; set; }

    /// <summary>Nombre descriptivo (ej: "Ruta Norte - Matutina")</summary>
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Fecha para la que está programada la ruta</summary>
    public DateTime ScheduledDate { get; set; } = DateTime.UtcNow;

    /// <summary>Token para acceso del repartidor</summary>
    [Required, MaxLength(64)]
    public string DriverToken { get; set; } = string.Empty;

    public RouteStatus Status { get; set; } = RouteStatus.Pending;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>Última posición conocida del repartidor</summary>
    public double? CurrentLatitude { get; set; }
    public double? CurrentLongitude { get; set; }
    public DateTime? LastLocationUpdate { get; set; }

    // Relaciones
    public ICollection<Order> Orders { get; set; } = new List<Order>();
    public ICollection<Delivery> Deliveries { get; set; } = new List<Delivery>();

    // Nuevo: Chat historial
    public ICollection<ChatMessage> ChatMessages { get; set; } = new List<ChatMessage>();
}

public enum RouteStatus
{
    Pending = 0,
    Active = 1,
    Completed = 2,
    Canceled = 3 // Agregado por si se cancela
}