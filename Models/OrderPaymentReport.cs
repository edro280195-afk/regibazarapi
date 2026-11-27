namespace EntregasApi.Models;

public enum PaymentReportStatus
{
    Pending,
    Confirmed,
    Rejected
}

/// <summary>
/// Aviso de pago reportado por la clienta desde el enlace de su pedido (Tarjeta aprobada en MP,
/// Transferencia u OXXO). No descuenta el saldo del pedido hasta que la dueña revise sus
/// transacciones y lo confirme de recibido.
/// </summary>
public class OrderPaymentReport
{
    public int Id { get; set; }

    public int OrderId { get; set; }
    public Order Order { get; set; } = null!;

    public decimal Amount { get; set; }
    public string Method { get; set; } = "Transferencia"; // Tarjeta | Transferencia | OXXO
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public string? ProofImageUrl { get; set; }

    public PaymentReportStatus Status { get; set; } = PaymentReportStatus.Pending;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewedBy { get; set; }
    public string? RejectionReason { get; set; }

    /// <summary>Id del OrderPayment creado cuando la dueña confirma de recibido.</summary>
    public int? ConfirmedPaymentId { get; set; }
}
