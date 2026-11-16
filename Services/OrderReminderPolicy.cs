using EntregasApi.Models;

namespace EntregasApi.Services;

/// <summary>
/// Reglas puras de la "campanita" de pedidos (validación, hora del aviso y texto del push).
/// Sin base de datos ni reloj propio, para poder probarlas con facilidad.
/// </summary>
public static class OrderReminderPolicy
{
    public const int MaxNoteLength = 500;

    /// <summary>Máxima anticipación permitida (12 h).</summary>
    public const int MaxLeadMinutes = 720;

    /// <summary>
    /// Tolerancia al guardar una hora que "ya pasó": cubre el desfase entre el reloj del
    /// celular y el del servidor. Más allá de esto se rechaza (un aviso en el pasado no tiene sentido).
    /// </summary>
    public static readonly TimeSpan PastTolerance = TimeSpan.FromMinutes(2);

    /// <summary>Normaliza a UTC. Un valor sin zona se asume UTC (el frontend siempre manda ISO con Z).</summary>
    public static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    /// <summary>Devuelve el mensaje de error, o null si la solicitud es válida.</summary>
    public static string? Validate(DateTime targetAtUtc, int leadMinutes, string? note, DateTime nowUtc)
    {
        if (leadMinutes < 0 || leadMinutes > MaxLeadMinutes)
            return $"La anticipación debe estar entre 0 y {MaxLeadMinutes} minutos.";

        if (note != null && note.Trim().Length > MaxNoteLength)
            return $"La nota no puede exceder {MaxNoteLength} caracteres.";

        if (targetAtUtc == default)
            return "Falta la hora que pidió la clienta.";

        if (targetAtUtc < nowUtc - PastTolerance)
            return "La hora de la clienta ya pasó. Elige una hora futura.";

        return null;
    }

    public static DateTime ComputeRemindAt(DateTime targetAtUtc, int leadMinutes)
        => targetAtUtc.AddMinutes(-leadMinutes);

    /// <summary>La campanita solo tiene sentido mientras el pedido siga pendiente de entregar.</summary>
    public static bool IsOrderActionable(OrderStatus status)
        => status != OrderStatus.Delivered && status != OrderStatus.Canceled;

    /// <summary>Normaliza la nota: vacía → null, y recorta espacios.</summary>
    public static string? CleanNote(string? note)
        => string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    /// <summary>Título y cuerpo del push que se manda a la dueña.</summary>
    public static (string Title, string Body) BuildMessage(
        string clientName, DateTime targetAtUtc, string? note, DateTime nowUtc, TimeZoneInfo zone)
    {
        var name = string.IsNullOrWhiteSpace(clientName) ? "una clienta" : clientName.Trim();
        var title = $"🔔 Entrega de {name}";

        var targetLocal = TimeZoneInfo.ConvertTimeFromUtc(targetAtUtc, zone);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);

        var body = $"Hora acordada: {DayLabel(targetLocal, nowLocal)} {FormatClock(targetLocal)} ({Relative(targetAtUtc - nowUtc)}).";

        var cleanNote = CleanNote(note);
        if (cleanNote != null)
            body += $" 📝 {cleanNote}";

        return (title, body);
    }

    /// <summary>"4:05 pm" — manual para no depender de la cultura/ICU del servidor.</summary>
    public static string FormatClock(DateTime local)
    {
        var hour12 = local.Hour % 12 == 0 ? 12 : local.Hour % 12;
        var suffix = local.Hour < 12 ? "am" : "pm";
        return $"{hour12}:{local.Minute:00} {suffix}";
    }

    public static string DayLabel(DateTime targetLocal, DateTime nowLocal)
    {
        var days = (targetLocal.Date - nowLocal.Date).Days;
        return days switch
        {
            0 => "hoy",
            1 => "mañana",
            -1 => "ayer",
            _ => targetLocal.ToString("dd/MM")
        };
    }

    public static string Relative(TimeSpan delta)
    {
        var minutes = (int)Math.Round(Math.Abs(delta.TotalMinutes));
        if (minutes < 1) return "ahora";

        var text = minutes < 60
            ? $"{minutes} min"
            : minutes % 60 == 0
                ? $"{minutes / 60} h"
                : $"{minutes / 60} h {minutes % 60} min";

        return delta > TimeSpan.Zero ? $"en {text}" : $"hace {text}";
    }
}
