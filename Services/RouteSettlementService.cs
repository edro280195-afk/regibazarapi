using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;

namespace EntregasApi.Services;

public class RouteSettlementService : IRouteSettlementService
{
    private readonly AppDbContext _db;

    public RouteSettlementService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<Dictionary<int, List<ActiveTandaInfoDto>>> GetActiveTandasForClientsAsync(IEnumerable<int> clientIds)
    {
        var distinctIds = clientIds.Where(id => id > 0).Distinct().ToList();
        if (!distinctIds.Any())
            return new Dictionary<int, List<ActiveTandaInfoDto>>();

        var participants = await _db.TandaParticipants
            .Include(p => p.Tanda)
                .ThenInclude(t => t!.Product)
            .Include(p => p.Payments)
            .Where(p => distinctIds.Contains(p.CustomerId) && p.Status == "Active" && p.Tanda != null && p.Tanda.Status == "Active")
            .ToListAsync();

        var result = new Dictionary<int, List<ActiveTandaInfoDto>>();

        foreach (var p in participants)
        {
            if (p.Tanda == null) continue;

            var weeklyAmount = p.WeeklyAmount ?? p.Tanda.WeeklyAmount;
            var totalPaid = p.Payments.Sum(x => x.AmountPaid);
            var totalCost = p.Tanda.TotalWeeks * weeklyAmount;
            var balanceRemaining = Math.Max(0m, totalCost - totalPaid);

            // Determinar próxima semana pendiente
            var paidWeeks = p.Payments.Select(x => x.WeekNumber).ToHashSet();
            int nextPendingWeek = 1;
            for (int w = 1; w <= p.Tanda.TotalWeeks; w++)
            {
                if (!paidWeeks.Contains(w))
                {
                    nextPendingWeek = w;
                    break;
                }
            }

            var dto = new ActiveTandaInfoDto(
                ParticipantId: p.Id,
                TandaId: p.TandaId,
                TandaName: p.Tanda.Name,
                ProductName: p.Tanda.Product?.Name,
                NextPendingWeek: nextPendingWeek,
                TotalWeeks: p.Tanda.TotalWeeks,
                WeeklyAmount: weeklyAmount,
                Currency: p.Currency ?? p.Tanda.Currency ?? "MXN",
                TotalPaid: totalPaid,
                BalanceRemaining: balanceRemaining
            );

            if (!result.ContainsKey(p.CustomerId))
            {
                result[p.CustomerId] = new List<ActiveTandaInfoDto>();
            }
            result[p.CustomerId].Add(dto);
        }

        return result;
    }

    public async Task<bool> RecordDriverTandaPaymentAsync(int routeId, DriverTandaPaymentRequest req)
    {
        var route = await _db.DeliveryRoutes.FindAsync(routeId);
        if (route == null) return false;

        string changeNote = "";
        if (req.CashReceived.HasValue && req.CashChange.HasValue && req.CashChange > 0)
        {
            changeNote = $" [Recibió ${req.CashReceived:N2}, entregó ${req.CashChange:N2} de cambio]";
        }

        string fullNotes = string.IsNullOrWhiteSpace(req.Notes)
            ? $"Cobrado en ruta #{route.Id} por repartidor.{changeNote}"
            : $"{req.Notes.Trim()}{changeNote}";

        if (req.TandaParticipantId.HasValue)
        {
            var participant = await _db.TandaParticipants
                .Include(p => p.Tanda)
                .Include(p => p.Payments)
                .FirstOrDefaultAsync(p => p.Id == req.TandaParticipantId.Value);

            if (participant != null)
            {
                int weekNumber = req.WeekNumber ?? 1;
                if (!req.WeekNumber.HasValue)
                {
                    var paidWeeks = participant.Payments.Select(x => x.WeekNumber).ToHashSet();
                    for (int w = 1; w <= (participant.Tanda?.TotalWeeks ?? 52); w++)
                    {
                        if (!paidWeeks.Contains(w)) { weekNumber = w; break; }
                    }
                }

                var payment = new TandaPayment
                {
                    Id = Guid.NewGuid(),
                    ParticipantId = participant.Id,
                    WeekNumber = weekNumber,
                    AmountPaid = req.Amount,
                    PaymentDate = DateTime.UtcNow,
                    PaymentMethod = req.PaymentMethod,
                    IsVerified = true,
                    Notes = fullNotes,
                    DeliveryRouteId = route.Id,
                    DeliveryId = req.DeliveryId,
                    RegisteredBy = "Driver"
                };

                _db.TandaPayments.Add(payment);
                await _db.SaveChangesAsync();
                return true;
            }
        }

        // Si no tiene participante en sistema o no se encontró, se registra como ingreso extra de tanda
        var extra = new RouteExtraIncome
        {
            DeliveryRouteId = route.Id,
            DeliveryId = req.DeliveryId,
            Amount = req.Amount,
            PaymentMethod = req.PaymentMethod,
            IncomeType = "Tanda",
            ClientName = req.ClientName ?? "Clienta",
            Notes = fullNotes,
            CreatedAt = DateTime.UtcNow
        };

        _db.RouteExtraIncomes.Add(extra);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<RouteSettlementDto> SettleRouteAsync(int routeId, RouteSettlementRequest req, string baseUrl)
    {
        var route = await _db.DeliveryRoutes.FindAsync(routeId);
        if (route == null) throw new InvalidOperationException("Ruta no encontrada.");

        // Obtener esperado primero
        var preSettlement = await GetSettlementAsync(routeId, baseUrl);
        var difference = req.FinalCashActual - preSettlement.ExpectedCash;

        route.FinalCashActual = req.FinalCashActual;
        route.FinalCashBreakdownJson = req.Breakdown != null ? JsonSerializer.Serialize(req.Breakdown) : null;
        route.SettlementDifference = difference;
        route.SettledAt = DateTime.UtcNow;
        route.SettlementNotes = req.Notes;

        await _db.SaveChangesAsync();

        return await GetSettlementAsync(routeId, baseUrl);
    }

    public async Task<RouteSettlementDto> GetSettlementAsync(int routeId, string baseUrl)
    {
        var route = await _db.DeliveryRoutes
            .Include(r => r.Deliveries).ThenInclude(d => d.Order).ThenInclude(o => o!.Payments)
            .Include(r => r.Deliveries).ThenInclude(d => d.Order).ThenInclude(o => o!.Client)
            .Include(r => r.Deliveries).ThenInclude(d => d.TandaParticipant).ThenInclude(p => p!.Client)
            .Include(r => r.Expenses)
            .Include(r => r.TandaPayments).ThenInclude(tp => tp.Participant).ThenInclude(p => p!.Client)
            .Include(r => r.TandaPayments).ThenInclude(tp => tp.Participant).ThenInclude(p => p!.Tanda)
            .Include(r => r.ExtraIncomes)
            .FirstOrDefaultAsync(r => r.Id == routeId);

        if (route == null) throw new InvalidOperationException("Ruta no encontrada.");

        // Desglose de Caja Chica inicial
        CashDenominationsDto? initialBreakdown = null;
        if (!string.IsNullOrWhiteSpace(route.InitialCashBreakdownJson))
        {
            try
            {
                initialBreakdown = JsonSerializer.Deserialize<CashDenominationsDto>(route.InitialCashBreakdownJson);
            }
            catch { /* ignorar error de json */ }
        }

        // Desglose final de billetes contados
        CashDenominationsDto? finalBreakdown = null;
        if (!string.IsNullOrWhiteSpace(route.FinalCashBreakdownJson))
        {
            try
            {
                finalBreakdown = JsonSerializer.Deserialize<CashDenominationsDto>(route.FinalCashBreakdownJson);
            }
            catch { /* ignorar error de json */ }
        }

        // Pagos de órdenes entregadas
        var orderPaymentsList = new List<RouteSettlementOrderPaymentDto>();
        decimal orderCashTotal = 0m;
        decimal orderTransferTotal = 0m;
        decimal orderDepositTotal = 0m;

        foreach (var delivery in route.Deliveries)
        {
            if (delivery.Order?.Payments != null)
            {
                foreach (var p in delivery.Order.Payments)
                {
                    // Contamos los pagos registrados en esta ruta / chofer o de este pedido entregado
                    var method = p.Method ?? "Efectivo";
                    if (method.Contains("Efectivo", StringComparison.OrdinalIgnoreCase))
                        orderCashTotal += p.Amount;
                    else if (method.Contains("Transfer", StringComparison.OrdinalIgnoreCase))
                        orderTransferTotal += p.Amount;
                    else
                        orderDepositTotal += p.Amount;

                    orderPaymentsList.Add(new RouteSettlementOrderPaymentDto(
                        OrderId: delivery.OrderId ?? 0,
                        ClientName: delivery.Order.Client?.Name ?? "Cliente",
                        Address: delivery.Order.Client?.Address,
                        Total: delivery.Order.Total,
                        AmountPaid: p.Amount,
                        Method: method,
                        Notes: p.Notes,
                        Date: p.Date
                    ));
                }
            }
        }

        var orderTotal = orderCashTotal + orderTransferTotal + orderDepositTotal;

        // Pagos de tanda formal y extra
        var tandaPaymentsList = new List<RouteSettlementTandaPaymentDto>();
        decimal tandaCashTotal = 0m;
        decimal tandaTransferTotal = 0m;

        foreach (var tp in route.TandaPayments)
        {
            var isCash = tp.PaymentMethod?.Contains("Efectivo", StringComparison.OrdinalIgnoreCase) ?? true;
            if (isCash) tandaCashTotal += tp.AmountPaid;
            else tandaTransferTotal += tp.AmountPaid;

            tandaPaymentsList.Add(new RouteSettlementTandaPaymentDto(
                PaymentId: tp.Id,
                ClientName: tp.Participant?.Client?.Name ?? tp.Participant?.CustomerName ?? "Clienta",
                TandaName: tp.Participant?.Tanda?.Name ?? "Tanda",
                WeekNumber: tp.WeekNumber,
                Amount: tp.AmountPaid,
                Method: tp.PaymentMethod ?? "Efectivo",
                Notes: tp.Notes,
                Date: tp.PaymentDate
            ));
        }

        foreach (var ei in route.ExtraIncomes)
        {
            var isCash = ei.PaymentMethod.Contains("Efectivo", StringComparison.OrdinalIgnoreCase);
            if (isCash) tandaCashTotal += ei.Amount;
            else tandaTransferTotal += ei.Amount;

            tandaPaymentsList.Add(new RouteSettlementTandaPaymentDto(
                PaymentId: null,
                ClientName: ei.ClientName ?? "Clienta",
                TandaName: $"{ei.IncomeType} (Extra)",
                WeekNumber: null,
                Amount: ei.Amount,
                Method: ei.PaymentMethod,
                Notes: ei.Notes,
                Date: ei.CreatedAt
            ));
        }

        var tandaTotal = tandaCashTotal + tandaTransferTotal;

        // Gastos
        var expensesList = route.Expenses.Select(e => new DriverExpenseDto(
            e.Id,
            e.DeliveryRouteId ?? route.Id,
            route.Name,
            e.Amount,
            e.ExpenseType,
            e.Date,
            e.Notes,
            e.EvidencePath,
            e.CreatedAt
        )).ToList();

        var expensesTotal = expensesList.Sum(e => e.Amount);

        // Conciliación de Efectivo
        // Efectivo esperado en mano = Caja chica + Cobros efectivo pedidos + Cobros efectivo tandas - Gastos
        var initialCash = route.InitialCash;
        var expectedCash = initialCash + orderCashTotal + tandaCashTotal - expensesTotal;
        var actualCash = route.FinalCashActual;
        decimal? difference = actualCash.HasValue ? actualCash.Value - expectedCash : null;

        string settlementStatus;
        if (!actualCash.HasValue)
        {
            settlementStatus = "Pendiente";
        }
        else if (difference == 0m)
        {
            settlementStatus = "Cuadrado";
        }
        else if (difference > 0m)
        {
            settlementStatus = "Sobrante";
        }
        else
        {
            settlementStatus = "Faltante";
        }

        int totalDeliveries = route.Deliveries.Count;
        int deliveredCount = route.Deliveries.Count(d => d.Status == DeliveryStatus.Delivered);
        int failedCount = route.Deliveries.Count(d => d.Status == DeliveryStatus.NotDelivered);

        string cleanBaseUrl = baseUrl.TrimEnd('/');
        string webReportUrl = $"{cleanBaseUrl}/repartidor/{route.DriverToken}/corte";

        var settlementDto = new RouteSettlementDto(
            RouteId: route.Id,
            RouteName: route.Name,
            DriverToken: route.DriverToken,
            Status: route.Status.ToString(),
            StartedAt: route.StartedAt,
            CompletedAt: route.CompletedAt,
            SettledAt: route.SettledAt,
            InitialCash: initialCash,
            InitialBreakdown: initialBreakdown,
            ExpectedCash: expectedCash,
            ActualCash: actualCash,
            FinalBreakdown: finalBreakdown,
            Difference: difference,
            SettlementStatus: settlementStatus,
            Notes: route.SettlementNotes,
            TotalDeliveries: totalDeliveries,
            DeliveredCount: deliveredCount,
            FailedCount: failedCount,
            OrderCashTotal: orderCashTotal,
            OrderTransferTotal: orderTransferTotal,
            OrderDepositTotal: orderDepositTotal,
            OrderTotal: orderTotal,
            TandaCashTotal: tandaCashTotal,
            TandaTransferTotal: tandaTransferTotal,
            TandaTotal: tandaTotal,
            ExpensesTotal: expensesTotal,
            OrderPayments: orderPaymentsList,
            TandaPayments: tandaPaymentsList,
            Expenses: expensesList,
            WhatsAppShareText: "",
            WebReportUrl: webReportUrl
        );

        var whatsappText = GenerateWhatsAppText(settlementDto);
        return settlementDto with { WhatsAppShareText = whatsappText };
    }

    private static string GenerateWhatsAppText(RouteSettlementDto s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("✨ *CORTE DE RUTA — REGI BAZAR* 🎀");
        sb.AppendLine($"🚗 *Ruta:* {s.RouteName}");
        sb.AppendLine($"📅 *Fecha:* {DateTime.UtcNow:dd/MM/yyyy}");
        if (s.StartedAt != null && s.CompletedAt != null)
        {
            sb.AppendLine($"⏰ *Horario:* {s.StartedAt.Value:hh:mm tt} - {s.CompletedAt.Value:hh:mm tt}");
        }
        sb.AppendLine();
        sb.AppendLine("📊 *RESUMEN DE ENTREGAS*");
        sb.AppendLine($"• Entregadas: {s.DeliveredCount} de {s.TotalDeliveries} ✅");
        if (s.FailedCount > 0) sb.AppendLine($"• Fallidas: {s.FailedCount} ❌");
        sb.AppendLine();
        sb.AppendLine("💰 *MOVIMIENTO DE CAJA*");
        sb.AppendLine($"• (+) Caja chica inicial: ${s.InitialCash:N2}");
        sb.AppendLine($"• (+) Pedidos en efectivo: ${s.OrderCashTotal:N2}");
        if (s.TandaCashTotal > 0)
            sb.AppendLine($"• (+) Tandas en efectivo: ${s.TandaCashTotal:N2}");
        if (s.ExpensesTotal > 0)
            sb.AppendLine($"• (-) Gastos de ruta: -${s.ExpensesTotal:N2}");
        sb.AppendLine("────────────────────");
        sb.AppendLine($"💵 *Efectivo esperado en mano:* ${s.ExpectedCash:N2}");

        if (s.ActualCash.HasValue)
        {
            sb.AppendLine($"💵 *Efectivo físico entregado:* ${s.ActualCash.Value:N2}");
            if (s.Difference == 0)
                sb.AppendLine("🎯 *BALANCE:* ¡CUADRADO PERFECTO! ✓ ($0.00) 🎉");
            else if (s.Difference > 0)
                sb.AppendLine($"✨ *BALANCE:* Sobrante de +${s.Difference.Value:N2} MXN");
            else
                sb.AppendLine($"⚠️ *BALANCE:* ¡FALTANTE DE -${Math.Abs(s.Difference.Value):N2} MXN! ⚠️");
        }
        else
        {
            sb.AppendLine("⏳ *ARQUEO:* Pendiente de conteo físico");
        }

        if (s.FinalBreakdown != null)
        {
            sb.AppendLine();
            sb.AppendLine("🔍 *DESGLOSE DE BILLETES CONTADOS:*");
            if (s.FinalBreakdown.B1000 > 0) sb.AppendLine($"• $1,000 MXN: {s.FinalBreakdown.B1000} (${s.FinalBreakdown.B1000 * 1000:N0})");
            if (s.FinalBreakdown.B500 > 0)  sb.AppendLine($"• $500 MXN: {s.FinalBreakdown.B500} (${s.FinalBreakdown.B500 * 500:N0})");
            if (s.FinalBreakdown.B200 > 0)  sb.AppendLine($"• $200 MXN: {s.FinalBreakdown.B200} (${s.FinalBreakdown.B200 * 200:N0})");
            if (s.FinalBreakdown.B100 > 0)  sb.AppendLine($"• $100 MXN: {s.FinalBreakdown.B100} (${s.FinalBreakdown.B100 * 100:N0})");
            if (s.FinalBreakdown.B50 > 0)   sb.AppendLine($"• $50 MXN: {s.FinalBreakdown.B50} (${s.FinalBreakdown.B50 * 50:N0})");
            if (s.FinalBreakdown.B20 > 0)   sb.AppendLine($"• $20 MXN: {s.FinalBreakdown.B20} (${s.FinalBreakdown.B20 * 20:N0})");
            if (s.FinalBreakdown.Coins > 0) sb.AppendLine($"• Monedas/Feria: ${s.FinalBreakdown.Coins:N2}");
            if (s.FinalBreakdown.TotalUsd > 0)
                sb.AppendLine($"• Dólares: ${s.FinalBreakdown.TotalUsd:N2} USD (Tipo cambio: ${s.FinalBreakdown.ExchangeRate:N2} = ${s.FinalBreakdown.TotalUsd * s.FinalBreakdown.ExchangeRate:N2} MXN)");
        }

        if (s.OrderTransferTotal > 0 || s.OrderDepositTotal > 0 || s.TandaTransferTotal > 0)
        {
            sb.AppendLine();
            sb.AppendLine("💳 *OTROS MÉTODOS (NO EFECTIVO)*");
            if (s.OrderTransferTotal > 0) sb.AppendLine($"• Pedidos por transferencia: ${s.OrderTransferTotal:N2}");
            if (s.OrderDepositTotal > 0)  sb.AppendLine($"• Pedidos por depósito: ${s.OrderDepositTotal:N2}");
            if (s.TandaTransferTotal > 0) sb.AppendLine($"• Tandas por transferencia: ${s.TandaTransferTotal:N2}");
        }

        if (s.TandaPayments.Any())
        {
            sb.AppendLine();
            sb.AppendLine("🌸 *COBROS DE TANDA EN RUTA:*");
            foreach (var tp in s.TandaPayments)
            {
                var sem = tp.WeekNumber.HasValue ? $" (Sem {tp.WeekNumber})" : "";
                sb.AppendLine($"• {tp.ClientName}: ${tp.Amount:N2} - {tp.TandaName}{sem} [{tp.Method}]");
                if (!string.IsNullOrWhiteSpace(tp.Notes))
                    sb.AppendLine($"  ↳ {tp.Notes}");
            }
        }

        if (s.Expenses.Any())
        {
            sb.AppendLine();
            sb.AppendLine("⛽ *GASTOS REPORTADOS:*");
            foreach (var exp in s.Expenses)
            {
                var note = !string.IsNullOrWhiteSpace(exp.Notes) ? $" ({exp.Notes})" : "";
                sb.AppendLine($"• {exp.ExpenseType}: ${exp.Amount:N2}{note}");
            }
        }

        if (!string.IsNullOrWhiteSpace(s.Notes))
        {
            sb.AppendLine();
            sb.AppendLine($"📝 *Observaciones:* {s.Notes}");
        }

        sb.AppendLine();
        sb.AppendLine("🔗 *Ver detalle completo en la web:*");
        sb.AppendLine(s.WebReportUrl);

        return sb.ToString();
    }
}
