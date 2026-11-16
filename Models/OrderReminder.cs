using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models;

/// <summary>
/// "Campanita" de un pedido: recordatorio privado de la dueña. Sirve para clientas que piden
/// entrega a cierta hora (ej. "hoy después de las 4:00 pm") o con alguna condición
/// (ej. "estará en su trabajo, otra dirección") y no se quiere que se olvide.
///
/// Es independiente del horario de entrega (<see cref="Order.ScheduledDeliveryDate"/> /
/// <see cref="Order.DeliveryTime"/>): guardar o quitar la campanita NO cambia la fecha
/// programada ni el vencimiento del enlace de la clienta.
///
/// Un pedido tiene a lo mucho una campanita (índice único por OrderId).
/// </summary>
[Table("OrderReminders")]
public class OrderReminder
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int OrderId { get; set; }
    [ForeignKey(nameof(OrderId))]
    public Order Order { get; set; } = null!;

    /// <summary>Hora que pidió la clienta (UTC).</summary>
    public DateTime TargetAt { get; set; }

    /// <summary>Minutos de anticipación con los que debe sonar el aviso (0 = a la hora exacta).</summary>
    public int LeadMinutes { get; set; }

    /// <summary>Cuándo debe sonar (UTC) = TargetAt - LeadMinutes. Se guarda para poder indexarlo.</summary>
    public DateTime RemindAt { get; set; }

    /// <summary>Nota libre de la dueña (ej. "en su trabajo, otra dirección").</summary>
    [MaxLength(500)]
    public string? Note { get; set; }

    /// <summary>Cuándo se procesó el aviso. Null = aún pendiente de sonar.</summary>
    public DateTime? SentAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
