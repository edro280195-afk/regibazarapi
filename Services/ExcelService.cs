using OfficeOpenXml;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public interface IExcelService
{
    Task<ExcelUploadResult> ProcessExcelAsync(Stream fileStream, string frontendBaseUrl);
}

public class ExcelService : IExcelService
{
    private readonly AppDbContext _db;
    private readonly ITokenService _tokenService;

    public ExcelService(AppDbContext db, ITokenService tokenService)
    {
        _db = db;
        _tokenService = tokenService;
    }

    public async Task<ExcelUploadResult> ProcessExcelAsync(Stream fileStream, string frontendBaseUrl)
    {
        var warnings = new List<string>();
        var settings = await _db.AppSettings.FirstAsync();

        using var package = new ExcelPackage(fileStream);
        var worksheet = package.Workbook.Worksheets[0];

        if (worksheet == null) throw new InvalidOperationException("Sin hojas.");
        var rowCount = worksheet.Dimension?.Rows ?? 0;
        if (rowCount < 2) throw new InvalidOperationException("Sin datos.");

        var colMap = DetectColumns(worksheet);

        var clientData = new Dictionary<string, (string ClientType, OrderType OrderType, List<(string Product, int Qty, decimal Price)> Items)>(
            StringComparer.OrdinalIgnoreCase);

        for (int row = 2; row <= rowCount; row++)
        {
            var clientName = worksheet.Cells[row, colMap["cliente"]].Text?.Trim();
            var product = worksheet.Cells[row, colMap["articulo"]].Text?.Trim();

            var qtyVal = worksheet.Cells[row, colMap["cantidad"]].Value;
            var priceVal = colMap.ContainsKey("precio") ? worksheet.Cells[row, colMap["precio"]].Value : null;

            var clientTypeText = colMap.ContainsKey("tipo") ? worksheet.Cells[row, colMap["tipo"]].Text?.Trim() : "Nueva";

            var methodText = colMap.ContainsKey("metodo") ? worksheet.Cells[row, colMap["metodo"]].Text?.Trim().ToLower() : "";
            var orderType = OrderType.Delivery;

            if (methodText.Contains("pick") || methodText.Contains("recoger") || methodText.Contains("local"))
            {
                orderType = OrderType.PickUp;
            }

            if (string.IsNullOrEmpty(clientName) || string.IsNullOrEmpty(product)) continue;

            int qty = 1;
            if (qtyVal is double qd) qty = (int)qd;
            else if (qtyVal is int qi) qty = qi;
            else if (qtyVal != null) int.TryParse(qtyVal.ToString()?.Trim(), out qty);
            if (qty <= 0) qty = 1;

            decimal price = 0;
            if (priceVal is double pd) price = (decimal)pd;
            else if (priceVal is decimal pdc) price = pdc;
            else if (priceVal != null)
                decimal.TryParse(priceVal.ToString()?.Trim(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out price);

            if (!clientData.ContainsKey(clientName))
            {
                clientData[clientName] = (clientTypeText!, orderType, new List<(string, int, decimal)>());
            }

            var currentData = clientData[clientName];
            if (!string.IsNullOrEmpty(clientTypeText) && clientTypeText != "Nueva")
                currentData.ClientType = clientTypeText;

            if (orderType == OrderType.PickUp)
                currentData.OrderType = OrderType.PickUp;

            currentData.Items.Add((product, qty, price));
            clientData[clientName] = currentData;
        }

        int clientsCreated = 0;
        int ordersCreated = 0;
        var orderSummaries = new List<OrderSummaryDto>();

        foreach (var (clientName, (clientType, orderType, items)) in clientData)
        {
            var client = await _db.Clients.FirstOrDefaultAsync(c => c.Name.ToLower() == clientName.ToLower());
            if (client == null)
            {
                client = new Client { Name = clientName, Type = clientType };
                _db.Clients.Add(client);
                await _db.SaveChangesAsync();
                clientsCreated++;
            }
            else if (!string.IsNullOrEmpty(clientType))
            {
                client.Type = clientType;
            }

            var orderToProcess = await _db.Orders
                .Include(o => o.Items)
                .Include(o => o.Client)
                .FirstOrDefaultAsync(o => o.ClientId == client.Id && o.Status == Models.OrderStatus.Pending);

            if (orderToProcess == null)
            {
                var accessToken = _tokenService.GenerateAccessToken();
                orderToProcess = new Order
                {
                    ClientId = client.Id,
                    AccessToken = accessToken,
                    ShippingCost = (orderType == OrderType.PickUp) ? 0 : settings.DefaultShippingCost,
                    ExpiresAt = DateTime.UtcNow.AddHours(settings.LinkExpirationHours),
                    Status = Models.OrderStatus.Pending,
                    OrderType = orderType,
                    Items = new List<OrderItem>()
                };
                _db.Orders.Add(orderToProcess);
                ordersCreated++;
            }
            else
            {
                if (orderType == OrderType.PickUp)
                {
                    orderToProcess.OrderType = OrderType.PickUp;
                    orderToProcess.ShippingCost = 0;
                }
            }

            foreach (var (product, qty, price) in items)
            {
                orderToProcess.Items.Add(new OrderItem
                {
                    ProductName = product,
                    Quantity = qty,
                    UnitPrice = price,
                    LineTotal = price * qty
                });
            }

            decimal subtotal = orderToProcess.Items.Sum(i => i.LineTotal);
            orderToProcess.Subtotal = subtotal;
            orderToProcess.Total = subtotal + orderToProcess.ShippingCost;

            await _db.SaveChangesAsync();

            if (orderToProcess.Client == null) orderToProcess.Client = client;

            orderSummaries.Add(MapToSummary(orderToProcess, client, frontendBaseUrl));
        }

        return new ExcelUploadResult(ordersCreated, clientsCreated, orderSummaries, warnings);
    }

    private Dictionary<string, int> DetectColumns(ExcelWorksheet ws)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var colCount = ws.Dimension?.Columns ?? 0;

        for (int col = 1; col <= colCount; col++)
        {
            var header = ws.Cells[1, col].Text?.Trim().ToLower() ?? "";

            if (header.Contains("articulo") || header.Contains("producto"))
                map["articulo"] = col;
            else if (header.Contains("cantidad") || header.Contains("qty"))
                map["cantidad"] = col;
            else if (header.Contains("precio") || header.Contains("costo"))
                map["precio"] = col;
            else if (header.Contains("tipo") || header.Contains("clasificacion"))
                map["tipo"] = col;
            else if (header.Contains("cliente") || header.Contains("nombre"))
                map["cliente"] = col;
            else if (header.Contains("metodo") || header.Contains("entrega") || header.Contains("envio"))
                map["metodo"] = col;
        }

        if (!map.ContainsKey("articulo") || !map.ContainsKey("cantidad") || !map.ContainsKey("cliente"))
            throw new InvalidOperationException("Faltan columnas requeridas (Articulo, Cantidad, Cliente).");

        return map;
    }

    // ... resto del archivo ExcelService ...

    public static OrderSummaryDto MapToSummary(Order order, Client? client, string frontendBaseUrl)
    {
        string finalType = "Nueva";
        if (client != null && !string.IsNullOrEmpty(client.Type) && client.Type != "None")
        {
            finalType = client.Type;
        }

        var paymentDtos = (order.Payments ?? new List<OrderPayment>())
            .Select(p => new OrderPaymentDto(p.Id, p.OrderId, p.Amount, p.Method, p.Date, p.RegisteredBy, p.Notes))
            .ToList();

        return new OrderSummaryDto(
            order.Id,
            client?.Name ?? "Cliente Desconocido",
            order.Status.ToString(),
            order.Total,
            Link: $"{frontendBaseUrl}/pedido/{order.AccessToken}",
            order.Items.Count,
            order.OrderType.ToString(),
            order.CreatedAt,
            finalType,
            client?.Phone,
            client?.Address,
            order.PostponedAt,
            order.PostponedNote,
            Items: order.Items.Select(i => new OrderItemDto(
                i.Id, i.ProductName, i.Quantity, i.UnitPrice, i.LineTotal
            )).ToList(),
            ShippingCost: order.ShippingCost,
            AccessToken: order.AccessToken,
            ExpiresAt: order.ExpiresAt,
            Subtotal: order.Subtotal,
            Payments: paymentDtos,
            AmountPaid: order.AmountPaid,
            BalanceDue: order.BalanceDue,
            AdvancePayment: order.AdvancePayment,
            PaymentMethod: order.PaymentMethod
        );
    }
}