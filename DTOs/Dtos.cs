using System.ComponentModel.DataAnnotations;

namespace EntregasApi.DTOs;

// ── Auth ──
public record LoginRequest(string Email, string Password);
public record LoginResponse(string Token, string Name, DateTime ExpiresAt);
public record RegisterRequest(string Name, string Email, string Password);

// ── General ──
public record PagedResult<T>(
    List<T> Items,
    int TotalCount,
    int CurrentPage,
    int PageSize
);

// ── Excel Upload ──
public record ExcelUploadResult(
    int OrdersCreated,
    int ClientsCreated,
    List<OrderSummaryDto> Orders,
    List<string> Warnings
);

public record OrderSummaryDto(
    int Id,
    string ClientName,
    string Status,
    decimal Total,
    string Link,
    int ItemsCount,
    string OrderType,
    DateTime CreatedAt,
    string ClientType,

    string? ClientPhone,
    string? ClientAddress,
    DateTime? PostponedAt,
    string? PostponedNote,
    decimal Subtotal,
    decimal ShippingCost,
    string AccessToken,
    DateTime ExpiresAt,
    List<OrderItemDto> Items,
    // Nuevo: Libro de Pagos
    List<OrderPaymentDto> Payments = null!,
    decimal AmountPaid = 0m,
    decimal BalanceDue = 0m,
    // Legacy (retrocompatibilidad)
    decimal AdvancePayment = 0m,
    string? PaymentMethod = null,
    // SalesPeriod (Corte)
    int? SalesPeriodId = null,
    string? SalesPeriodName = null
);

public record OrderTrackingDto(
    int Id,
    string ClientName,
    string Status,
    decimal Total,
    int ItemsCount,
    string OrderType,
    List<OrderItemDto> Items,
    DateTime CreatedAt,
    string ClientType
);

public record OrderItemDto(
    int Id,
    string ProductName,
    int Quantity,
    decimal UnitPrice,
    decimal LineTotal
);

public record ManualOrderRequest(
    string ClientName,
    string? ClientPhone,
    string? ClientAddress,
    string? ClientType,
    string OrderType,
    List<ManualOrderItem> Items,
    DateTime? PostponedAt = null,
    string? PostponedNote = null,
    string Status = "Pending"
);
public record ManualOrderItem(
    string ProductName,
    int Quantity,
    decimal UnitPrice
);

public record ManualOrderItemRequest(
    string ProductName,
    int Quantity,
    decimal UnitPrice
);

// ── Delivery Route ──
public record CreateRouteRequest(List<int> OrderIds);

public record RouteDto(
    int Id,
    string DriverToken,
    string DriverLink,
    string Status,
    DateTime CreatedAt,
    DateTime? StartedAt,
    List<RouteDeliveryDto> Deliveries,
    List<DriverExpenseDto>? Expenses = null
);

public record RouteDeliveryDto(
    int DeliveryId,
    int OrderId,
    int SortOrder,
    string ClientName,
    string? ClientAddress,
    double? Latitude,
    double? Longitude,
    string Status,
    decimal Total,
    DateTime? DeliveredAt,
    string? Notes,
    string? FailureReason,
    List<string> EvidenceUrls,
    string? ClientPhone,
    string? PaymentMethod,
    List<OrderPaymentDto>? Payments = null,
    decimal AmountPaid = 0m,
    decimal BalanceDue = 0m
)
{
    public int Id => DeliveryId;
    public string? Address => ClientAddress;
}

public record CreateAdminExpenseRequest(decimal Amount, string ExpenseType, DateTime Date, string? Notes, int? DeliveryRouteId);
public record UpdateAdminExpenseRequest(decimal Amount, string ExpenseType, DateTime Date, string? Notes, int? DeliveryRouteId);

// ── Driver ──
public record UpdateLocationRequest(double Latitude, double Longitude);
public record CompleteDeliveryRequest(string? Notes, string? PaymentsJson);
public record PaymentInputDto(decimal Amount, string Method, string? Notes);
public record FailDeliveryRequest(string Reason, string? Notes);

// ── Client View ──
public record ClientOrderView(
    int ClientId,
    string ClientName,
    List<OrderItemDto> Items,
    decimal Subtotal,
    decimal ShippingCost,
    decimal Total,
    string Status,
    DateTime? EstimatedArrival,
    DriverLocationDto? DriverLocation,
    int? QueuePosition = null,
    int? TotalDeliveries = null,
    bool IsCurrentDelivery = false,
    int? DeliveriesAhead = null,
    double? ClientLatitude = null,
    double? ClientLongitude = null,   
    DateTime? CreatedAt = null,
    string? ClientType = null,
    string? ClientAddress = null,
    decimal AdvancePayment = 0m,
    List<OrderPaymentDto>? Payments = null,
    decimal AmountPaid = 0m,
    decimal BalanceDue = 0m
);

// ── OrderPayment ──
public record OrderPaymentDto(
    int Id,
    int OrderId,
    decimal Amount,
    string Method,
    DateTime Date,
    string RegisteredBy,
    string? Notes
);

public record AddPaymentRequest
{
    [Required]
    public decimal Amount { get; init; }

    [Required, MaxLength(50)]
    public string Method { get; init; } = "Efectivo";

    public string? RegisteredBy { get; init; }  // ✅ nullable

    [MaxLength(500)]
    public string? Notes { get; init; }
};

public record DriverLocationDto(
    double Latitude,
    double Longitude,
    DateTime LastUpdate
);

// ── Dashboard ──
public record DashboardDto(
    int TotalClients,
    int TotalOrders,
    int PendingOrders,
    int DeliveredOrders,
    int NotDeliveredOrders,
    int ActiveRoutes,
    decimal TotalRevenue,
    decimal RevenueMonth,
    decimal RevenueToday,
    decimal TotalInvestment,
    int TotalCashOrders,
    decimal TotalCashAmount,
    int TotalTransferOrders,
    decimal TotalTransferAmount,
    int TotalDepositOrders,
    decimal TotalDepositAmount,
    // Chart data — eliminates N+1 calls from frontend
    List<MonthlySalesDto> SalesByMonth,
    int ClientsNueva,
    int ClientsFrecuente,
    int OrdersDelivery,
    int OrdersPickUp,
    ActivePeriodSummaryDto? ActivePeriod = null
);

public record ActivePeriodSummaryDto(
    int Id,
    string Name,
    decimal TotalSales,
    decimal TotalInvested,
    decimal NetProfit,
    decimal CollectedAmount = 0m
);

public record MonthlySalesDto(string Month, decimal Sales);
public record CommonProductDto(string Name, int Count, decimal TypicalPrice);

// ── Reports ──
public record ReportDto(
    // Financiero
    decimal TotalRevenue,
    decimal TotalInvestment,
    decimal TotalExpenses,
    decimal NetProfit,
    // Pedidos
    int TotalOrders,
    int PendingOrders,
    int InRouteOrders,
    int DeliveredOrders,
    int NotDeliveredOrders,
    int CanceledOrders,
    int DeliveryOrders,
    int PickUpOrders,
    decimal AvgTicket,
    List<TopProductDto> TopProducts,
    List<DailyCountDto> OrdersByDay,
    // Rutas
    int TotalRoutes,
    int CompletedRoutes,
    decimal SuccessRate,
    decimal TotalDriverExpenses,
    // Clientas
    int NewClients,
    int FrequentClients,
    int ActiveClients,
    List<TopClientDto> TopClients,
    // Cobros
    int CashOrders,
    decimal CashAmount,
    int TransferOrders,
    decimal TransferAmount,
    int DepositOrders,
    decimal DepositAmount,
    int UnassignedPaymentOrders,
    // Proveedores
    List<SupplierSummaryDto> SupplierSummaries
);

public record TopProductDto(string Name, int Quantity, decimal Revenue);
public record DailyCountDto(string Date, int Count, decimal Amount);
public record TopClientDto(string Name, int Orders, decimal TotalSpent);
public record SupplierSummaryDto(string Name, decimal TotalInvested, int InvestmentCount);

// ── Glow Up (IG Story) ──
public record GlowUpReportDto(
    string MonthName,
    int TotalDeliveries,
    string TopProduct,
    int NewClients
);

public record OrderStatsDto(
    int Total,
    int Pending,
    decimal PendingAmount,
    decimal CollectedToday
);

public enum OrderType
{
    Delivery = 0, // Domicilio (Default)
    PickUp = 1    // Recoger en tienda
}

public enum OrderStatus
{
    Pending = 0,      // Pendiente
    InRoute = 1,      // En Ruta (Solo para Delivery)
    Delivered = 2,    // Entregado
    NotDelivered = 3, // No Entregado (intento fallido)
    Canceled = 4,     // Cancelado (Nuevo)
    Postponed = 5,     // Pospuesto (Nuevo)
    Confirmed = 6,  // Clienta confirmó el pedido
    Sent = 7
}

public record UpdateOrderStatusRequest(
    string? Status,              // ✅ nullable
    string? OrderType,           // ✅ nullable — Angular no lo manda aquí
    DateTime? PostponedAt,
    string? PostponedNote
);

public enum ClientTag
{
    None = 0,         // Normal
    RisingStar = 1,   // En Ascenso 🚀
    Vip = 2,          // Consentida 👑
    Blacklist = 3     // Lista Negra 🚫
}

public record SupplierDto(
    int Id,
    string Name,
    string? ContactName,
    string? Phone,
    string? Notes,
    DateTime CreatedAt,
    decimal TotalInvested = 0m
);

public record CreateSupplierRequest
{
    [Required, MaxLength(200)]
    public string Name { get; init; } = string.Empty;

    [MaxLength(200)]
    public string? ContactName { get; init; }

    [MaxLength(50)]
    public string? Phone { get; init; }

    [MaxLength(500)]
    public string? Notes { get; init; }
}

public record UpdateSupplierRequest
{
    [Required, MaxLength(200)]
    public string Name { get; init; } = string.Empty;

    [MaxLength(200)]
    public string? ContactName { get; init; }

    [MaxLength(50)]
    public string? Phone { get; init; }

    [MaxLength(500)]
    public string? Notes { get; init; }
}

// ── Investment DTOs ──

public record InvestmentDto(
    int Id,
    int SupplierId,
    decimal Amount,
    DateTime Date,
    string? Notes,
    DateTime CreatedAt,
    string Currency,
    decimal ExchangeRate,
    decimal TotalMXN,
    int? SalesPeriodId = null,
    string? SalesPeriodName = null
);

public record CreateInvestmentRequest
{
    [Required]
    public decimal Amount { get; init; }

    [Required]
    public DateTime Date { get; init; }

    [MaxLength(500)]
    public string? Notes { get; init; }

    public string Currency { get; set; } = "MXN";
    public decimal? ExchangeRate { get; set; }

    public int? SalesPeriodId { get; init; }
}

public record DriverExpenseDto(
    int Id,
    int? DriverRouteId,
    string? DriverName,
    decimal Amount,
    string ExpenseType,
    DateTime Date,
    string? Notes,
    string? EvidenceUrl,
    DateTime CreatedAt
);

public record CreateDriverExpenseRequest
{
    [Required]
    public decimal Amount { get; init; }

    [Required, MaxLength(50)]
    public string ExpenseType { get; init; } = "Gasolina";

    [MaxLength(500)]
    public string? Notes { get; init; }
}

public record FinancialReportDto
{
    public string Period { get; init; } = string.Empty;
    public DateTime StartDate { get; init; }
    public DateTime EndDate { get; init; }
    public decimal TotalIncome { get; init; }
    public decimal TotalInvestment { get; init; }
    public decimal TotalExpenses { get; init; }
    public decimal NetProfit { get; init; }
    public FinancialDetailsDto Details { get; init; } = new();
}

public record FinancialDetailsDto
{
    public List<InvestmentLineDto> Investments { get; init; } = new();
    public List<IncomeLineDto> Incomes { get; init; } = new();
    public List<ExpenseLineDto> Expenses { get; init; } = new();
}

public record InvestmentLineDto(
    int Id,
    string SupplierName,
    decimal Amount,
    DateTime Date,
    string? Notes
);

public record IncomeLineDto(
    int Id,
    string ClientName,
    decimal Total,
    string OrderType,
    DateTime CreatedAt
);

public record ExpenseLineDto(
    int Id,
    int? DriverRouteId,
    string? RouteName,
    string? DriverName,
    decimal Amount,
    string ExpenseType,
    DateTime Date,
    string? Notes,
    string? EvidenceUrl
);

// DTO para actualizar la orden completa
public record UpdateOrderDetailsRequest(
    string? Status,
    string? OrderType,
    DateTime? PostponedAt,
    string? PostponedNote,
    string ClientName,
    string? ClientAddress,
    string? ClientPhone,
    List<string>? Tags,
    string? DeliveryTime,
    string? PickupDate,
    decimal? ShippingCost = null,
    decimal? AdvancePayment = null,
    int? SalesPeriodId = null
);

// DTO para actualizar un producto individual
public record UpdateOrderItemRequest(
    string ProductName,
    int Quantity,
    decimal UnitPrice
);

public record SendMessageRequest(string Text);

// ── SalesPeriods (Cortes de Venta) ──
public record SalesPeriodDto(
    int Id,
    string Name,
    DateTime StartDate,
    DateTime EndDate,
    bool IsActive,
    DateTime CreatedAt
);

public record CreateSalesPeriodRequest
{
    [Required, MaxLength(200)]
    public string Name { get; init; } = string.Empty;

    [Required]
    public DateTime StartDate { get; init; }

    [Required]
    public DateTime EndDate { get; init; }
}

public record PeriodReportDto(
    int PeriodId,
    string PeriodName,
    decimal TotalSales,
    decimal TotalInvestments,
    decimal NetProfit,
    List<PeriodInvestmentBySupplierDto> InvestmentsBySupplier
);

public record PeriodInvestmentBySupplierDto(
    string SupplierName,
    decimal TotalInvested,
    int InvestmentCount
);

public record SyncSalesPeriodRequest(
    DateTime InvStartDate,
    DateTime InvEndDate,
    DateTime OrderStartDate,
    DateTime OrderEndDate
);