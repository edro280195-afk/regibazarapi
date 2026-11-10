using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

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

    // ── CAJA CHICA Y CORTE DE RUTA ──
    /// <summary>Fondo inicial con el que el chofer arranca la ruta</summary>
    [Column(TypeName = "decimal(12, 2)")]
    public decimal InitialCash { get; set; } = 0m;

    /// <summary>Desglose JSON de billetes y monedas con los que inició</summary>
    public string? InitialCashBreakdownJson { get; set; }

    /// <summary>Tipo de cambio USD usado al inicio</summary>
    [Column(TypeName = "decimal(12, 4)")]
    public decimal InitialExchangeRate { get; set; } = 18.0m;

    /// <summary>Efectivo físico entregado por el chofer al finalizar ruta</summary>
    [Column(TypeName = "decimal(12, 2)")]
    public decimal? FinalCashActual { get; set; }

    /// <summary>Desglose JSON de billetes y monedas contados al cierre</summary>
    public string? FinalCashBreakdownJson { get; set; }

    /// <summary>Diferencia de caja calculada al cierre (Actual - Esperado)</summary>
    [Column(TypeName = "decimal(12, 2)")]
    public decimal? SettlementDifference { get; set; }

    /// <summary>Fecha y hora en que se realizó el corte</summary>
    public DateTime? SettledAt { get; set; }

    /// <summary>Observaciones del corte de caja</summary>
    [MaxLength(1000)]
    public string? SettlementNotes { get; set; }

    // Relaciones
    public ICollection<Order> Orders { get; set; } = new List<Order>();
    public ICollection<Delivery> Deliveries { get; set; } = new List<Delivery>();
    public ICollection<DriverExpense> Expenses { get; set; } = new List<DriverExpense>();
    public ICollection<TandaPayment> TandaPayments { get; set; } = new List<TandaPayment>();
    public ICollection<RouteExtraIncome> ExtraIncomes { get; set; } = new List<RouteExtraIncome>();

    // Chat historial
    public ICollection<ChatMessage> ChatMessages { get; set; } = new List<ChatMessage>();
}

public enum RouteStatus
{
    Pending = 0,
    Active = 1,
    Completed = 2,
    Canceled = 3 // Agregado por si se cancela
}
