namespace EntregasApi.Models;

public class AppSettings
{
    [System.ComponentModel.DataAnnotations.Key]
    public int Id { get; set; } = 1;

    /// <summary>Costo de envío por defecto en MXN</summary>
    public decimal DefaultShippingCost { get; set; } = 60m;

    /// <summary>Horas de vigencia del enlace de la clienta</summary>
    public int LinkExpirationHours { get; set; } = 72;
}
