using System.Text.Json;
using System.Text.Json.Serialization;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Hubs;
using EntregasApi.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public interface IMercadoPagoOrderService
{
    Task<CheckoutProPreferenceResponse> CreateOrderPreferenceAsync(Order order, string? webhookBaseUrl = null);
    Task<VerifyOrderPaymentResponse> ReconcileOrderPaymentAsync(long mpPaymentId, int? expectedOrderId = null);
    Task<MpPaymentLookupResult?> FetchPaymentAsync(long mpPaymentId);
}

public sealed class MpPaymentLookupResult
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("status_detail")]
    public string StatusDetail { get; set; } = "";

    [JsonPropertyName("external_reference")]
    public string? ExternalReference { get; set; }

    [JsonPropertyName("transaction_amount")]
    public decimal TransactionAmount { get; set; }

    [JsonPropertyName("payment_type_id")]
    public string? PaymentTypeId { get; set; }

    [JsonPropertyName("payment_method_id")]
    public string? PaymentMethodId { get; set; }
}

public class MercadoPagoOrderService : IMercadoPagoOrderService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AppDbContext _db;
    private readonly IHubContext<DeliveryHub> _hub;
    private readonly IPushNotificationService _push;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;

    public MercadoPagoOrderService(
        AppDbContext db,
        IHubContext<DeliveryHub> hub,
        IPushNotificationService push,
        IHttpClientFactory httpClientFactory,
        IConfiguration config)
    {
        _db = db;
        _hub = hub;
        _push = push;
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    public async Task<CheckoutProPreferenceResponse> CreateOrderPreferenceAsync(Order order, string? webhookBaseUrl = null)
    {
        var balanceDue = Math.Round(order.BalanceDue, 2, MidpointRounding.AwayFromZero);
        if (balanceDue <= 0m)
            throw new InvalidOperationException("Este pedido ya está liquidado.");

        var mpAccessToken = _config["MercadoPago:AccessToken"];
        if (string.IsNullOrWhiteSpace(mpAccessToken))
            throw new InvalidOperationException("MercadoPago:AccessToken no configurado.");

        var frontendUrl = (_config["App:FrontendUrl"] ?? "https://regibazar.com").TrimEnd('/');
        var returnUrl = $"{frontendUrl}/pedido/{order.AccessToken}";

        var configuredApiUrl = _config["App:ApiUrl"]?.TrimEnd('/');
        var notificationBase = !string.IsNullOrWhiteSpace(configuredApiUrl)
            ? configuredApiUrl
            : (!string.IsNullOrWhiteSpace(webhookBaseUrl) &&
               webhookBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
               !webhookBaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase))
                ? webhookBaseUrl.TrimEnd('/')
                : "https://regibazarapi.onrender.com";

        var itemSummary = order.Items != null && order.Items.Count > 0
            ? string.Join(", ", order.Items.Select(i => i.Quantity > 1 ? $"{i.Quantity}x {i.ProductName}" : i.ProductName))
            : $"Saldo pendiente del pedido #{order.Id}";
        if (itemSummary.Length > 240)
            itemSummary = itemSummary[..237] + "...";

        var (firstName, lastName) = SplitClientName(order.Client?.Name);
        var cleanPhone = CleanPhoneDigits(order.Client?.Phone);

        // No enviamos email fijo del comercio: Checkout Pro usa el correo real de la clienta
        // (su cuenta de Mercado Pago o el que capture al pagar), evitando falsos positivos antifraude.
        object? payer = null;
        if (!string.IsNullOrWhiteSpace(firstName) || !string.IsNullOrWhiteSpace(cleanPhone))
        {
            payer = new
            {
                name = firstName,
                surname = lastName,
                phone = !string.IsNullOrWhiteSpace(cleanPhone)
                    ? new { area_code = "52", number = cleanPhone }
                    : null
            };
        }

        var preferenceBody = new
        {
            items = new[]
            {
                new
                {
                    id = $"order-{order.Id}",
                    title = $"Pedido #{order.Id} - Regi Bazar",
                    description = itemSummary,
                    category_id = "retail",
                    quantity = 1,
                    currency_id = "MXN",
                    unit_price = balanceDue
                }
            },
            payer,
            payment_methods = new
            {
                excluded_payment_types = new[]
                {
                    new { id = "ticket" },
                    new { id = "atm" }
                }
            },
            back_urls = new
            {
                success = $"{returnUrl}?mp_return=success",
                pending = $"{returnUrl}?mp_return=pending",
                failure = $"{returnUrl}?mp_return=failure"
            },
            auto_return = "approved",
            external_reference = $"order_{order.Id}",
            statement_descriptor = "REGI BAZAR",
            notification_url = $"{notificationBase}/api/payments/webhook",
            metadata = new
            {
                order_id = order.Id,
                type = "order_payment"
            }
        };

        using var httpClient = _httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {mpAccessToken}");

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await httpClient.PostAsJsonAsync(
                "https://api.mercadopago.com/checkout/preferences",
                preferenceBody);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MercadoPago] Error de red creando preferencia para pedido #{order.Id}: {ex.Message}");
            throw new InvalidOperationException("No se pudo conectar con Mercado Pago. Intenta de nuevo.");
        }

        var rawBody = await httpResponse.Content.ReadAsStringAsync();
        Console.WriteLine($"[MercadoPago] Preference Pedido #{order.Id} HTTP {(int)httpResponse.StatusCode}");

        if (!httpResponse.IsSuccessStatusCode)
        {
            Console.WriteLine($"[MercadoPago] Preference Error Body: {rawBody}");
            throw new InvalidOperationException("Mercado Pago no pudo iniciar el pago en este momento. Intenta de nuevo.");
        }

        var pref = JsonSerializer.Deserialize<MpPreferenceResponse>(rawBody, JsonOpts);
        var initPoint = !string.IsNullOrWhiteSpace(pref?.InitPoint) ? pref.InitPoint : pref?.SandboxInitPoint;
        if (pref == null || string.IsNullOrWhiteSpace(pref.Id) || string.IsNullOrWhiteSpace(initPoint))
        {
            Console.WriteLine($"[MercadoPago] Preference inválida: {rawBody}");
            throw new InvalidOperationException("Respuesta inesperada de Mercado Pago.");
        }

        return new CheckoutProPreferenceResponse(pref.Id, initPoint, balanceDue);
    }

    public async Task<MpPaymentLookupResult?> FetchPaymentAsync(long mpPaymentId)
    {
        var mpAccessToken = _config["MercadoPago:AccessToken"];
        if (string.IsNullOrWhiteSpace(mpAccessToken))
        {
            Console.WriteLine("[MercadoPago] Error: MercadoPago:AccessToken no configurado.");
            return null;
        }

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {mpAccessToken}");

        var response = await client.GetAsync($"https://api.mercadopago.com/v1/payments/{mpPaymentId}");
        var rawBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"[MercadoPago] GET /v1/payments/{mpPaymentId} -> HTTP {(int)response.StatusCode}: {rawBody}");
            return null;
        }

        var mpPayment = JsonSerializer.Deserialize<MpPaymentLookupResult>(rawBody, JsonOpts);
        if (mpPayment != null)
        {
            Console.WriteLine(
                $"[MercadoPago] Payment #{mpPayment.Id}: status={mpPayment.Status}, " +
                $"status_detail={mpPayment.StatusDetail}, amount={mpPayment.TransactionAmount:F2}, " +
                $"ref={mpPayment.ExternalReference}");
        }

        return mpPayment;
    }

    public async Task<VerifyOrderPaymentResponse> ReconcileOrderPaymentAsync(long mpPaymentId, int? expectedOrderId = null)
    {
        var mpPayment = await FetchPaymentAsync(mpPaymentId);
        if (mpPayment == null)
        {
            return new VerifyOrderPaymentResponse(
                Status: "error",
                StatusDetail: "lookup_failed",
                Amount: 0m,
                Message: "No pudimos verificar tu pago con Mercado Pago todavía. Intenta de nuevo en unos segundos.",
                PaymentId: mpPaymentId,
                OrderPaid: false);
        }

        if (string.IsNullOrWhiteSpace(mpPayment.ExternalReference) ||
            !mpPayment.ExternalReference.StartsWith("order_", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(mpPayment.ExternalReference["order_".Length..], out var orderId))
        {
            return new VerifyOrderPaymentResponse(
                Status: "error",
                StatusDetail: "invalid_reference",
                Amount: mpPayment.TransactionAmount,
                Message: "Este pago no corresponde a un pedido válido.",
                PaymentId: mpPayment.Id,
                OrderPaid: false);
        }

        if (expectedOrderId.HasValue && orderId != expectedOrderId.Value)
        {
            return new VerifyOrderPaymentResponse(
                Status: "error",
                StatusDetail: "order_mismatch",
                Amount: 0m,
                Message: "Este pago no pertenece a este pedido.",
                PaymentId: mpPayment.Id,
                OrderPaid: false);
        }

        var order = await _db.Orders
            .Include(o => o.Payments)
            .Include(o => o.Client)
            .FirstOrDefaultAsync(o => o.Id == orderId);

        if (order == null)
        {
            Console.WriteLine($"[MercadoPago] Pedido #{orderId} no encontrado para MP#{mpPayment.Id}.");
            return new VerifyOrderPaymentResponse(
                Status: "error",
                StatusDetail: "order_not_found",
                Amount: mpPayment.TransactionAmount,
                Message: "Pedido no encontrado.",
                PaymentId: mpPayment.Id,
                OrderPaid: false);
        }

        var existingPayment = order.Payments?
            .FirstOrDefault(p => OrderPaymentPolicy.TryGetMercadoPagoId(p.Notes) == mpPayment.Id);

        // Regla crítica: SOLO acreditamos el pago en la BD cuando Mercado Pago reporta "approved".
        // Nunca acreditamos "in_process" ni "pending" para evitar falsos pagados.
        if (mpPayment.Status == "approved")
        {
            if (existingPayment == null)
            {
                var payment = new OrderPayment
                {
                    OrderId = order.Id,
                    Amount = mpPayment.TransactionAmount,
                    Method = "Tarjeta",
                    Date = DateTime.UtcNow,
                    RegisteredBy = "Mercado Pago",
                    Notes = $"MP#{mpPayment.Id} | {mpPayment.StatusDetail}"
                };
                _db.OrderPayments.Add(payment);

                if (order.Status is OrderStatus.Pending or OrderStatus.Postponed)
                {
                    order.Status = OrderStatus.Confirmed;
                }

                await _db.SaveChangesAsync();

                var newTotalPaid = (order.Payments?.Sum(p => p.Amount) ?? 0m);
                await NotifyDriverBalanceChangedAsync(order, newTotalPaid);
                await NotifyAdminsAsync(order, mpPayment.TransactionAmount, "aprobado");
            }
            else if (existingPayment.Notes != null &&
                     !existingPayment.Notes.Contains("accredited", StringComparison.OrdinalIgnoreCase) &&
                     !existingPayment.Notes.Contains("approved", StringComparison.OrdinalIgnoreCase))
            {
                // Pago legacy que existía como in_process y finalmente se aprobó
                existingPayment.Notes = $"MP#{mpPayment.Id} | {mpPayment.StatusDetail}";
                if (order.Status is OrderStatus.Pending or OrderStatus.Postponed)
                {
                    order.Status = OrderStatus.Confirmed;
                }

                await _db.SaveChangesAsync();
                await NotifyAdminsAsync(order, mpPayment.TransactionAmount, "confirmado");
            }
        }
        else if (OrderPaymentPolicy.ShouldRevertCardPayment(mpPayment.Status))
        {
            // Rechazado / cancelado / reembolsado / contracargo: si existía registrado, se retira.
            if (existingPayment != null)
            {
                var removedAmount = existingPayment.Amount;
                Console.WriteLine(
                    $"[MercadoPago] MP#{mpPayment.Id} quedó '{mpPayment.Status}' ({mpPayment.StatusDetail}): " +
                    $"se quita el pago #{existingPayment.Id} (${removedAmount:F2}) del pedido #{order.Id}.");

                _db.OrderPayments.Remove(existingPayment);
                await _db.SaveChangesAsync();

                var remainingPaid = order.Payments!.Where(p => p.Id != existingPayment.Id).Sum(p => p.Amount);
                await NotifyDriverBalanceChangedAsync(order, remainingPaid);

                var action = mpPayment.Status switch
                {
                    "rejected" => "rechazado (se quitó del pedido)",
                    "cancelled" => "cancelado (se quitó del pedido)",
                    "refunded" => "reembolsado (se quitó del pedido)",
                    _ => "con contracargo (se quitó del pedido)"
                };
                await NotifyAdminsAsync(order, removedAmount, action);
            }
            else
            {
                Console.WriteLine(
                    $"[MercadoPago] MP#{mpPayment.Id} quedó '{mpPayment.Status}' ({mpPayment.StatusDetail}) " +
                    $"para pedido #{order.Id} (no estaba acreditado, sin cambios).");
            }
        }

        var message = mpPayment.Status switch
        {
            "approved" => "¡Pago aprobado con éxito! Tu pedido ya quedó registrado 🎉",
            "in_process" or "pending" or "authorized" =>
                "Mercado Pago está verificando tu pago. En cuanto se apruebe, tu pedido se actualizará automáticamente 💕",
            "rejected" => GetRejectionMessage(mpPayment.StatusDetail),
            "cancelled" => "El pago fue cancelado. Puedes intentarlo de nuevo cuando gustes.",
            "refunded" or "charged_back" => "Este pago fue reembolsado.",
            _ => "No se pudo confirmar el pago. Puedes intentarlo de nuevo o elegir otro método."
        };

        return new VerifyOrderPaymentResponse(
            Status: mpPayment.Status,
            StatusDetail: mpPayment.StatusDetail,
            Amount: mpPayment.TransactionAmount,
            Message: message,
            PaymentId: mpPayment.Id,
            OrderPaid: order.BalanceDue <= 0m);
    }

    private async Task NotifyDriverBalanceChangedAsync(Order order, decimal totalPaid)
    {
        if (!order.DeliveryRouteId.HasValue) return;

        var route = await _db.DeliveryRoutes.FindAsync(order.DeliveryRouteId.Value);
        if (route == null) return;

        await _hub.Clients.Group($"Route_{route.DriverToken}").SendAsync("OrderDataChanged", new
        {
            OrderId = order.Id,
            ClientName = order.Client?.Name ?? "",
            NewTotal = order.Total,
            BalanceDue = Math.Max(0m, order.Total - totalPaid),
            ChangeType = "payment"
        });
    }

    private async Task NotifyAdminsAsync(Order order, decimal amount, string action)
    {
        var clientName = order.Client?.Name ?? "Clienta";

        await _hub.Clients.Group("Admins").SendAsync("DeliveryUpdate", new
        {
            OrderId = order.Id,
            Status = order.Status.ToString(),
            ChangeType = "card_payment_webhook",
            ClientName = clientName,
            AmountPaid = amount
        });

        await _push.SendNotificationToAdminsAsync(
            $"💳 Pago {action}: ${amount:F2}",
            $"Pedido #{order.Id} de {clientName} actualizado vía Mercado Pago.",
            tag: "card-payment-webhook");
    }

    private static (string? firstName, string? lastName) SplitClientName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return (null, null);
        var parts = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return (null, null);
        if (parts.Length == 1) return (parts[0], null);
        return (parts[0], string.Join(" ", parts.Skip(1)));
    }

    private static string? CleanPhoneDigits(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length > 10 && digits.StartsWith("52"))
            digits = digits[2..];
        return digits.Length >= 10 ? digits[^10..] : null;
    }

    public static string GetRejectionMessage(string? statusDetail) => statusDetail switch
    {
        "cc_rejected_insufficient_amount" => "Fondos insuficientes. Verifica tu saldo o intenta con otra tarjeta.",
        "cc_rejected_bad_filled_card_number" => "Número de tarjeta incorrecto.",
        "cc_rejected_bad_filled_date" => "Fecha de vencimiento incorrecta.",
        "cc_rejected_bad_filled_security_code" => "Código de seguridad incorrecto.",
        "cc_rejected_blacklist" => "No pudimos procesar esta tarjeta. Intenta con otra o con tu cuenta de Mercado Pago.",
        "cc_rejected_call_for_authorize" => "Tu banco requiere autorización para esta compra. Autorízala en tu app bancaria e intenta de nuevo.",
        "cc_rejected_card_disabled" => "Tu tarjeta no está activa para compras en línea. Actívala en tu app bancaria o usa otra tarjeta.",
        "cc_rejected_duplicated_payment" => "Ya se registró un pago igual hace unos momentos.",
        "cc_rejected_high_risk" => "Mercado Pago rechazó el pago por seguridad. Intenta pagando con tu cuenta de Mercado Pago o con otra tarjeta.",
        _ => "El pago fue rechazado. Puedes intentar con otra tarjeta o elegir Transferencia / OXXO / Efectivo."
    };

    private sealed class MpPreferenceResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("init_point")]
        public string InitPoint { get; set; } = "";

        [JsonPropertyName("sandbox_init_point")]
        public string SandboxInitPoint { get; set; } = "";
    }
}
