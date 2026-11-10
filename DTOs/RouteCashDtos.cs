using System.ComponentModel.DataAnnotations;

namespace EntregasApi.DTOs;

/// <summary>
/// Desglose de billetes y monedas (MXN y USD) para caja chica y arqueo de fin de ruta
/// </summary>
public class CashDenominationsDto
{
    public decimal Coins { get; set; } = 0m;

    // MXN: 20, 50, 100, 200, 500, 1000
    public int B20 { get; set; } = 0;
    public int B50 { get; set; } = 0;
    public int B100 { get; set; } = 0;
    public int B200 { get; set; } = 0;
    public int B500 { get; set; } = 0;
    public int B1000 { get; set; } = 0;

    // USD: 1, 5, 10, 20, 50, 100
    public int Usd1 { get; set; } = 0;
    public int Usd5 { get; set; } = 0;
    public int Usd10 { get; set; } = 0;
    public int Usd20 { get; set; } = 0;
    public int Usd50 { get; set; } = 0;
    public int Usd100 { get; set; } = 0;

    public decimal ExchangeRate { get; set; } = 18.0m;

    public decimal TotalMxn => Coins + (B20 * 20m) + (B50 * 50m) + (B100 * 100m) + (B200 * 200m) + (B500 * 500m) + (B1000 * 1000m);
    public decimal TotalUsd => (Usd1 * 1m) + (Usd5 * 5m) + (Usd10 * 10m) + (Usd20 * 20m) + (Usd50 * 50m) + (Usd100 * 100m);
    public decimal TotalInMxn => TotalMxn + (TotalUsd * ExchangeRate);
}

public record StartRouteRequest(
    decimal? InitialCash,
    CashDenominationsDto? Breakdown,
    decimal? ExchangeRate
);

public record ActiveTandaInfoDto(
    Guid ParticipantId,
    Guid TandaId,
    string TandaName,
    string? ProductName,
    int NextPendingWeek,
    int TotalWeeks,
    decimal WeeklyAmount,
    string Currency,
    decimal TotalPaid,
    decimal BalanceRemaining
);

public record DriverTandaPaymentRequest(
    int? DeliveryId,
    Guid? TandaParticipantId,
    string? ClientName,
    int? WeekNumber,
    decimal Amount,
    string PaymentMethod,
    string? Notes,
    decimal? CashReceived,
    decimal? CashChange
);

public record RouteSettlementRequest(
    decimal FinalCashActual,
    CashDenominationsDto? Breakdown,
    string? Notes
);

public record RouteSettlementOrderPaymentDto(
    int OrderId,
    string ClientName,
    string? Address,
    decimal Total,
    decimal AmountPaid,
    string Method,
    string? Notes,
    DateTime Date
);

public record RouteSettlementTandaPaymentDto(
    Guid? PaymentId,
    string ClientName,
    string TandaName,
    int? WeekNumber,
    decimal Amount,
    string Method,
    string? Notes,
    DateTime Date
);

public record RouteSettlementDto(
    int RouteId,
    string RouteName,
    string DriverToken,
    string Status,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    DateTime? SettledAt,
    decimal InitialCash,
    CashDenominationsDto? InitialBreakdown,
    decimal ExpectedCash,
    decimal? ActualCash,
    CashDenominationsDto? FinalBreakdown,
    decimal? Difference,
    string SettlementStatus, // "Cuadrado", "Faltante", "Sobrante", "Pendiente"
    string? Notes,
    int TotalDeliveries,
    int DeliveredCount,
    int FailedCount,
    decimal OrderCashTotal,
    decimal OrderTransferTotal,
    decimal OrderDepositTotal,
    decimal OrderTotal,
    decimal TandaCashTotal,
    decimal TandaTransferTotal,
    decimal TandaTotal,
    decimal ExpensesTotal,
    List<RouteSettlementOrderPaymentDto> OrderPayments,
    List<RouteSettlementTandaPaymentDto> TandaPayments,
    List<DriverExpenseDto> Expenses,
    string WhatsAppShareText,
    string WebReportUrl
);
