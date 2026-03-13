using EntregasApi.Models;

namespace EntregasApi.Services;

public static class BackendExtensions
{
    public static string ToSpanishString(this OrderStatus status) => status switch
    {
        OrderStatus.Pending      => "Pendiente",
        OrderStatus.InRoute      => "En Camino",
        OrderStatus.Delivered    => "Entregada",
        OrderStatus.NotDelivered => "No Entregada",
        OrderStatus.Canceled     => "Cancelada",
        OrderStatus.Postponed    => "Pospuesta",
        OrderStatus.Confirmed    => "Confirmada",
        OrderStatus.Shipped      => "Enviada",
        _                        => status.ToString()
    };

    public static string ToSpanishString(this OrderType type) => type switch
    {
        OrderType.Delivery => "A domicilio",
        OrderType.PickUp   => "Recoger en tienda",
        _                  => type.ToString()
    };

    public static int CalculateLoyaltyPoints(this decimal total)
    {
        // El Total / 10 se redondea hacia abajo
        return (int)Math.Floor(total / 10m);
    }

    public static DateTime GetMexicoNow()
    {
        var mxZone = GetMexicoZone();
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, mxZone);
    }

    public static TimeZoneInfo GetMexicoZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Matamoros"); }
        catch { return TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time"); }
    }

    public static readonly string[] ValidPaymentMethods = { "Efectivo", "Transferencia", "OXXO", "Tarjeta" };

    public static double CalculateSimilarity(string source, string target)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target)) return 0;
        if (source == target) return 1.0;

        int n = source.Length;
        int m = target.Length;
        int[,] d = new int[n + 1, m + 1];

        for (int i = 0; i <= n; i++) d[i, 0] = i;
        for (int j = 0; j <= m; j++) d[0, j] = j;

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = (target[j - 1] == source[i - 1]) ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        return 1.0 - (double)d[n, m] / Math.Max(source.Length, target.Length);
    }
}
