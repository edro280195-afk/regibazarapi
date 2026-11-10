namespace EntregasApi.Models;

public class AppSettings
{
    [System.ComponentModel.DataAnnotations.Key]
    public int Id { get; set; } = 1;

    /// <summary>Costo de envío por defecto en MXN</summary>
    public decimal DefaultShippingCost { get; set; } = 60m;

    /// <summary>Horas de vigencia del enlace de la clienta</summary>
    public int LinkExpirationHours { get; set; } = 72;

    /// <summary>Tipo de cambio del día (MXN por 1 USD) fijado por la dueña</summary>
    public decimal DailyExchangeRate { get; set; } = 18.0m;

    /// <summary>Fecha y hora de última actualización del tipo de cambio</summary>
    public DateTime? ExchangeRateUpdatedAt { get; set; }
}
