using System.Text.RegularExpressions;
using EntregasApi.Models;

namespace EntregasApi.Services;

/// <summary>
/// Reglas para quitar un pago de un pedido y para reaccionar a lo que Mercado Pago reporta de un
/// pago con tarjeta. Son funciones puras (sin BD ni red) para poder probarlas aisladas.
///
/// Contexto: el pago con tarjeta desde el enlace de la clienta se registra como OrderPayment en
/// cuanto Mercado Pago responde "approved" o "in_process". Si después Mercado Pago lo rechaza o lo
/// cancela, ese dinero nunca llegó, pero el pedido seguía contando como pagado porque el monto
/// seguía sumando en Order.AmountPaid. De ahí salen estas reglas.
/// </summary>
public static class OrderPaymentPolicy
{
    private static readonly Regex MercadoPagoIdPattern = new(@"MP#(\d+)", RegexOptions.Compiled);

    /// <summary>
    /// Extrae el id de Mercado Pago que se guarda en las notas del pago ("MP#123 | accredited").
    /// Devuelve null si el pago no es de Mercado Pago (efectivo, transferencia, POS, etc.).
    /// </summary>
    public static long? TryGetMercadoPagoId(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        var match = MercadoPagoIdPattern.Match(notes);
        return match.Success && long.TryParse(match.Groups[1].Value, out var id) ? id : null;
    }

    /// <summary>
    /// Estados finales de Mercado Pago en los que el dinero NO está (o ya se devolvió), así que el
    /// pago debe salir del pedido. "approved" e "in_process" NO entran: ahí el dinero llegó o puede
    /// llegar.
    /// </summary>
    public static bool ShouldRevertCardPayment(string? mercadoPagoStatus) =>
        mercadoPagoStatus is "rejected" or "cancelled" or "refunded" or "charged_back";

    public enum RemovalDecision
    {
        /// <summary>Se puede quitar sin más.</summary>
        Allow,

        /// <summary>
        /// Mercado Pago aún lo tiene en revisión. Se puede quitar, pero si luego lo aprueba el
        /// webhook lo volverá a registrar, y con razón: el dinero sí llegó.
        /// </summary>
        AllowButMayReturn,

        /// <summary>Pertenece a un corte de caja cerrado: quitarlo descuadraría la caja.</summary>
        BlockCashSessionClosed,

        /// <summary>Mercado Pago lo tiene aprobado: el dinero sí llegó. Hay que reembolsarlo allá.</summary>
        BlockMercadoPagoApproved
    }

    /// <param name="cashSessionStatus">Estado de la caja del POS a la que pertenece el pago; null si no es de caja.</param>
    /// <param name="mercadoPagoStatus">Estado REAL en Mercado Pago; null si el pago no es de Mercado Pago o MP ya no lo conoce.</param>
    public static RemovalDecision EvaluateRemoval(SessionStatus? cashSessionStatus, string? mercadoPagoStatus)
    {
        // FinalCashExpected se calcula al cerrar la caja y queda congelado: borrar un pago de una
        // caja cerrada dejaría el corte descuadrado para siempre.
        if (cashSessionStatus == SessionStatus.Closed)
            return RemovalDecision.BlockCashSessionClosed;

        return mercadoPagoStatus switch
        {
            "approved" or "in_mediation" => RemovalDecision.BlockMercadoPagoApproved,
            "in_process" or "pending" or "authorized" => RemovalDecision.AllowButMayReturn,
            _ => RemovalDecision.Allow
        };
    }

    /// <summary>Mensaje para la dueña cuando el borrado se bloquea; null si no hay bloqueo.</summary>
    public static string? BlockMessage(RemovalDecision decision) => decision switch
    {
        RemovalDecision.BlockCashSessionClosed =>
            "Este pago pertenece a un corte de caja ya cerrado. Quitarlo descuadraría la caja.",
        RemovalDecision.BlockMercadoPagoApproved =>
            "Mercado Pago tiene este pago como APROBADO: el dinero sí llegó. " +
            "Para quitarlo del pedido, primero reembólsalo desde Mercado Pago.",
        _ => null
    };
}
