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
    Task<CardPaymentResultDto> ProcessOrderCardPaymentAsync(Order order, CardPaymentRequest req, string? webhookBaseUrl = null);
    Task ReconcileOrderPaymentAsync(long mpPaymentId);
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

    public async Task<CardPaymentResultDto> ProcessOrderCardPaymentAsync(
        Order order,
        CardPaymentRequest req,
        string? webhookBaseUrl = null)
    {
        var balanceDue = Math.Round(order.BalanceDue, 2, MidpointRounding.AwayFromZero);
        if (balanceDue <= 0m)
            throw new InvalidOperationException("Este pedido ya está liquidado.");

        var mpAccessToken = _config["MercadoPago:AccessToken"];
        if (string.IsNullOrWhiteSpace(mpAccessToken))
            throw new InvalidOperationException("MercadoPago:AccessToken no configurado.");

        var configuredApiUrl = _config["App:ApiUrl"]?.TrimEnd('/');
        var notificationBase = !string.IsNullOrWhiteSpace(configuredApiUrl)
            ? configuredApiUrl
            : (!string.IsNullOrWhiteSpace(webhookBaseUrl) &&
               webhookBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
               !webhookBaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase))
                ? webhookBaseUrl.TrimEnd('/')
                : "https://regibazarapi.onrender.com";

        var payerEmail = !string.IsNullOrWhiteSpace(req.PayerEmail) && req.PayerEmail.Contains('@')
            ? req.PayerEmail.Trim().ToLowerInvariant()
            : $"cliente.pedido{order.Id}@regibazar.com";

        var fullName = !string.IsNullOrWhiteSpace(req.CardholderName)
            ? req.CardholderName.Trim()
            : order.Client?.Name;
        var (firstName, lastName) = SplitClientName(fullName);
        var cleanPhone = CleanPhoneDigits(order.Client?.Phone);

        var itemSummary = order.Items != null && order.Items.Count > 0
            ? string.Join(", ", order.Items.Select(i => i.Quantity > 1 ? $"{i.Quantity}x {i.ProductName}" : i.ProductName))
            : $"Pedido #{order.Id} - Regi Bazar";
        if (itemSummary.Length > 240)
            itemSummary = itemSummary[..237] + "...";

        long? issuerId = long.TryParse(req.IssuerId, out var parsedIssuer) ? parsedIssuer : null;

        var paymentBody = new
        {
            transaction_amount = balanceDue,
            token = req.CardToken,
            description = $"Pedido #{order.Id} - Regi Bazar",
            installments = Math.Max(1, req.Installments),
            payment_method_id = req.PaymentMethodId,
            issuer_id = issuerId,
            statement_descriptor = "REGI BAZAR",
            notification_url = $"{notificationBase}/api/payments/webhook",
            external_reference = $"order_{order.Id}",
            payer = new
            {
                email = payerEmail,
                first_name = firstName,
                last_name = lastName
            },
            additional_info = new
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
                        unit_price = balanceDue
                    }
                },
                payer = new
                {
                    first_name = firstName,
                    last_name = lastName,
                    phone = !string.IsNullOrWhiteSpace(cleanPhone)
                        ? new { area_code = "52", number = cleanPhone }
                        : null
                }
            },
            metadata = new
            {
                order_id = order.Id,
                type = "order_payment"
            }
        };

        using var httpClient = _httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {mpAccessToken}");
        httpClient.DefaultRequestHeaders.Add("X-Idempotency-Key", Guid.NewGuid().ToString());

        // Header antifraude crítico de Mercado Pago (generado por security.js en el navegador)
        if (!string.IsNullOrWhiteSpace(req.DeviceSessionId))
        {
            httpClient.DefaultRequestHeaders.Add("X-meli-session-id", req.DeviceSessionId.Trim());
        }

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await httpClient.PostAsJsonAsync("https://api.mercadopago.com/v1/payments", paymentBody);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MercadoPago] Error de red en pedido #{order.Id}: {ex.Message}");
            throw new InvalidOperationException("No se pudo conectar con Mercado Pago. Intenta de nuevo.");
        }

        var rawBody = await httpResponse.Content.ReadAsStringAsync();
        Console.WriteLine(
            $"[MercadoPago] POST /v1/payments Pedido #{order.Id} HTTP {(int)httpResponse.StatusCode} " +
            $"(deviceSession={(string.IsNullOrWhiteSpace(req.DeviceSessionId) ? "NONE" : "OK")}): {rawBody}");

        if (!httpResponse.IsSuccessStatusCode)
        {
            string mpErrorMsg = "Error al procesar el pago con Mercado Pago.";
            try
            {
                var errDoc = JsonDocument.Parse(rawBody);
                if (errDoc.RootElement.TryGetProperty("message", out var msgEl))
                    mpErrorMsg = msgEl.GetString() ?? mpErrorMsg;
                if (errDoc.RootElement.TryGetProperty("cause", out var causeEl) && causeEl.GetArrayLength() > 0)
                {
                    var desc = causeEl[0].TryGetProperty("description", out var d) ? d.GetString() : null;
                    if (!string.IsNullOrEmpty(desc)) mpErrorMsg = desc;
                }
            }
            catch { }
            throw new InvalidOperationException(mpErrorMsg);
        }

        MpPaymentLookupResult? mpResult;
        try
        {
            mpResult = JsonSerializer.Deserialize<MpPaymentLookupResult>(rawBody, JsonOpts);
        }
        catch
        {
            throw new InvalidOperationException("Respuesta inesperada de Mercado Pago.");
        }

        if (mpResult == null)
            throw new InvalidOperationException("Error al procesar la respuesta de Mercado Pago.");

        // Regla de validación de la dueña: cuando Mercado Pago aprueba, creamos un aviso de pago
        // (OrderPaymentReport en estado Pending) para que la dueña revise su cuenta de Mercado Pago
        // y confirme de recibido antes de marcar el pedido como pagado.
        if (mpResult.Status == "approved")
        {
            await CreatePendingCardPaymentReportAsync(order, mpResult.Id, balanceDue, mpResult.StatusDetail);
        }

        var message = mpResult.Status switch
        {
            "approved" => "¡Pago aprobado por Mercado Pago! Avisamos a Regi Bazar para que confirme de recibido 💕",
            "in_process" => "Tu pago está en revisión por Mercado Pago. En cuanto se apruebe avisaremos a Regi Bazar 💕",
            "pending" => "Pago pendiente de confirmación.",
            "rejected" => GetRejectionMessage(mpResult.StatusDetail),
            _ => "No se pudo procesar el pago. Intenta con otra tarjeta."
        };

        return new CardPaymentResultDto(mpResult.Status, mpResult.StatusDetail, balanceDue, message, mpResult.Id);
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

    public async Task ReconcileOrderPaymentAsync(long mpPaymentId)
    {
        var mpPayment = await FetchPaymentAsync(mpPaymentId);
        if (mpPayment == null) return;

        if (string.IsNullOrWhiteSpace(mpPayment.ExternalReference) ||
            !mpPayment.ExternalReference.StartsWith("order_", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(mpPayment.ExternalReference["order_".Length..], out var orderId))
        {
            return;
        }

        var order = await _db.Orders
            .Include(o => o.Payments)
            .Include(o => o.PaymentReports)
            .Include(o => o.Client)
            .FirstOrDefaultAsync(o => o.Id == orderId);

        if (order == null)
        {
            Console.WriteLine($"[Webhook] Pedido #{orderId} no encontrado para MP#{mpPayment.Id}.");
            return;
        }

        var existingPayment = order.Payments?
            .FirstOrDefault(p => OrderPaymentPolicy.TryGetMercadoPagoId(p.Notes) == mpPayment.Id);
        var mpRef = $"MP#{mpPayment.Id}";
        var existingReport = order.PaymentReports?
            .FirstOrDefault(r => string.Equals(r.Reference, mpRef, StringComparison.OrdinalIgnoreCase));

        if (mpPayment.Status == "approved")
        {
            if (existingPayment == null && existingReport == null)
            {
                await CreatePendingCardPaymentReportAsync(
                    order,
                    mpPayment.Id,
                    mpPayment.TransactionAmount,
                    mpPayment.StatusDetail);
            }
            else if (existingPayment != null &&
                     existingPayment.Notes != null &&
                     !existingPayment.Notes.Contains("accredited", StringComparison.OrdinalIgnoreCase) &&
                     !existingPayment.Notes.Contains("approved", StringComparison.OrdinalIgnoreCase))
            {
                existingPayment.Notes = $"MP#{mpPayment.Id} | {mpPayment.StatusDetail}";
                if (order.Status is OrderStatus.Pending or OrderStatus.Postponed)
                    order.Status = OrderStatus.Confirmed;

                await _db.SaveChangesAsync();
                await NotifyAdminsAsync(order, mpPayment.TransactionAmount, "confirmado");
            }
        }
        else if (OrderPaymentPolicy.ShouldRevertCardPayment(mpPayment.Status))
        {
            if (existingReport != null && existingReport.Status == PaymentReportStatus.Pending)
            {
                existingReport.Status = PaymentReportStatus.Rejected;
                existingReport.ReviewedAt = DateTime.UtcNow;
                existingReport.ReviewedBy = "Mercado Pago (Webhook)";
                existingReport.RejectionReason = $"Mercado Pago cambió el estado a '{mpPayment.Status}' ({mpPayment.StatusDetail}).";
                await _db.SaveChangesAsync();
                await _hub.Clients.Group($"Order_{order.AccessToken}").SendAsync("DeliveryUpdate", new { OrderId = order.Id, ChangeType = "payment_report_rejected" });
            }

            if (existingPayment != null)
            {
                var removedAmount = existingPayment.Amount;
                Console.WriteLine(
                    $"[Webhook] MP#{mpPayment.Id} quedó '{mpPayment.Status}' ({mpPayment.StatusDetail}): " +
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
                    $"[Webhook] MP#{mpPayment.Id} quedó '{mpPayment.Status}' ({mpPayment.StatusDetail}) " +
                    $"para pedido #{order.Id} (no estaba acreditado).");
            }
        }
    }

    private async Task CreatePendingCardPaymentReportAsync(
        Order order,
        long mpPaymentId,
        decimal amount,
        string statusDetail)
    {
        var mpRef = $"MP#{mpPaymentId}";
        var alreadyReported = await _db.OrderPaymentReports
            .AnyAsync(r => r.OrderId == order.Id && r.Reference == mpRef);
        if (alreadyReported) return;

        var report = new OrderPaymentReport
        {
            OrderId = order.Id,
            Amount = amount,
            Method = "Tarjeta",
            Reference = mpRef,
            Notes = $"Aprobado por Mercado Pago ({statusDetail})",
            Status = PaymentReportStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
        _db.OrderPaymentReports.Add(report);
        await _db.SaveChangesAsync();

        var clientName = order.Client?.Name ?? "Clienta";
        await _hub.Clients.Group("Admins").SendAsync("DeliveryUpdate", new
        {
            OrderId = order.Id,
            Status = order.Status.ToString(),
            ChangeType = "payment_report",
            ClientName = clientName,
            AmountPaid = amount
        });

        await _hub.Clients.Group($"Order_{order.AccessToken}").SendAsync("DeliveryUpdate", new
        {
            OrderId = order.Id,
            ChangeType = "payment_report"
        });

        await _push.SendNotificationToAdminsAsync(
            $"💳 Pago por confirmar: ${amount:F2} ({clientName})",
            $"Pedido #{order.Id} · {mpRef}. Revisa tu Mercado Pago y confirma de recibido.",
            url: "/admin/orders",
            tag: "payment-report");
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
            ChangeType = "card_payment",
            ClientName = clientName,
            AmountPaid = amount
        });

        await _push.SendNotificationToAdminsAsync(
            $"💳 Pago {action}: ${amount:F2}",
            $"Pedido #{order.Id} de {clientName} con tarjeta vía Mercado Pago.",
            tag: "card-payment");
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
        "cc_rejected_insufficient_amount" => "Fondos insuficientes. Verifica tu saldo.",
        "cc_rejected_bad_filled_card_number" => "Número de tarjeta incorrecto.",
        "cc_rejected_bad_filled_date" => "Fecha de vencimiento incorrecta.",
        "cc_rejected_bad_filled_security_code" => "Código de seguridad incorrecto.",
        "cc_rejected_blacklist" => "Tarjeta no autorizada. Intenta con otra.",
        "cc_rejected_call_for_authorize" => "Tu banco requiere autorización. Autorízala en tu app bancaria e intenta de nuevo.",
        "cc_rejected_card_disabled" => "Tarjeta deshabilitada para compras en línea. Actívala en tu app bancaria o usa otra.",
        "cc_rejected_duplicated_payment" => "Pago duplicado detectado.",
        "cc_rejected_high_risk" => "Pago rechazado por validación de seguridad. Intenta con otra tarjeta o método de pago.",
        _ => "Pago rechazado. Intenta con otra tarjeta o método de pago."
    };
}
