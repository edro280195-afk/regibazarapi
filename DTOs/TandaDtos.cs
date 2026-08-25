using System.ComponentModel.DataAnnotations;

namespace EntregasApi.DTOs;

public class CreateTandaDto
{
    [Required]
    public Guid ProductId { get; set; }

    [Required, MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    [Range(1, 52)]
    public int TotalWeeks { get; set; }

    [Range(typeof(decimal), "0.01", "9999999999")]
    public decimal WeeklyAmount { get; set; }

    [Range(typeof(decimal), "0", "9999999999")]
    public decimal PenaltyAmount { get; set; } = 0;

    [Required]
    public DateTime StartDate { get; set; }

    [MaxLength(10)]
    public string Currency { get; set; } = "MXN";

    public decimal? ItemCost { get; set; }

    public decimal? ExchangeRate { get; set; }

    [Required, MinLength(1)]
    public List<CreateTandaParticipantDto> Participants { get; set; } = new();
}

public class CreateTandaParticipantDto
{
    [Required]
    public int CustomerId { get; set; }

    [Required]
    public int AssignedTurn { get; set; }

    public string? Variant { get; set; }

    public decimal? WeeklyAmount { get; set; }

    [MaxLength(10)]
    public string? Currency { get; set; }

    public decimal? ItemCost { get; set; }

    public decimal? ExchangeRate { get; set; }
}

public class AddParticipantDto
{
    [Required]
    public Guid TandaId { get; set; }

    [Required]
    public int CustomerId { get; set; }

    [Required]
    public int AssignedTurn { get; set; }

    public string? Variant { get; set; }

    public decimal? WeeklyAmount { get; set; }

    [MaxLength(10)]
    public string? Currency { get; set; }

    public decimal? ItemCost { get; set; }

    public decimal? ExchangeRate { get; set; }
}
public class RegisterPaymentDto
{
    [Required]
    public Guid ParticipantId { get; set; }

    [Range(1, 52)]
    public int WeekNumber { get; set; }

    [Range(typeof(decimal), "0.01", "9999999999")]
    public decimal AmountPaid { get; set; }

    [Range(typeof(decimal), "0", "9999999999")]
    public decimal PenaltyPaid { get; set; } = 0;

    public DateTime? PaymentDate { get; set; }

    public bool IsVerified { get; set; } = true;

    [MaxLength(500)]
    public string? Notes { get; set; }
}

public class CreateTandaProductDto
{
    [Required, MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    public decimal BasePrice { get; set; } = 0;
}

public class TandaViewDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public int TotalWeeks { get; set; }
    public decimal WeeklyAmount { get; set; }
    public DateTime StartDate { get; set; }
    public int CurrentWeek { get; set; }
    public List<TandaParticipantViewDto> Participants { get; set; } = new();
}

public class TandaParticipantViewDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int AssignedTurn { get; set; }
    public bool HasPaidCurrentWeek { get; set; }
    public List<int> PaidWeeks { get; set; } = new();
    public bool IsWinnerThisWeek { get; set; }
    public bool IsDelivered { get; set; }
    public string? Variant { get; set; }
    public decimal? WeeklyAmount { get; set; }
}

public class UpdateTandaDto
{
    public Guid? ProductId { get; set; }

    [Required, MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    [Range(1, 52)]
    public int TotalWeeks { get; set; }

    [Range(typeof(decimal), "0.01", "9999999999")]
    public decimal WeeklyAmount { get; set; }

    [Range(typeof(decimal), "0", "9999999999")]
    public decimal PenaltyAmount { get; set; }

    [Required]
    public DateTime StartDate { get; set; }

    [MaxLength(10)]
    public string? Currency { get; set; }

    public decimal? ItemCost { get; set; }

    public decimal? ExchangeRate { get; set; }

    [RegularExpression("^(Draft|Active|Completed|Cancelled)$")]
    public string? Status { get; set; }
}

// ── DTOs de Respuesta (Admin) ──
public class TandaDto
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int TotalWeeks { get; set; }
    public decimal WeeklyAmount { get; set; }
    public decimal PenaltyAmount { get; set; }
    public DateTime StartDate { get; set; }
    public string Currency { get; set; } = "MXN";
    public decimal? ItemCost { get; set; }
    public decimal? ExchangeRate { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string? AccessToken { get; set; }
    public int CurrentWeek { get; set; }
    public int ParticipantCount { get; set; }
    public int AvailablePlaces { get; set; }
    public int PaidInstallments { get; set; }
    public int TotalInstallments { get; set; }
    public decimal ExpectedAmount { get; set; }
    public decimal CollectedAmount { get; set; }
    public decimal BalanceDue { get; set; }
    public decimal ProgressPercentage { get; set; }
    public TandaProductDto? Product { get; set; }
    public List<TandaParticipantDto>? Participants { get; set; }
}

public class TandaParticipantDto
{
    public Guid Id { get; set; }
    public Guid TandaId { get; set; }
    public int CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public int AssignedTurn { get; set; }
    public decimal? WeeklyAmount { get; set; }
    public string? Currency { get; set; }
    public decimal? ItemCost { get; set; }
    public decimal? ExchangeRate { get; set; }
    public bool IsDelivered { get; set; }
    public DateTime? DeliveryDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Variant { get; set; }
    public decimal ExpectedAmount { get; set; }
    public decimal CollectedAmount { get; set; }
    public decimal BalanceDue { get; set; }
    public int PaidInstallments { get; set; }
    public List<TandaPaymentDto>? Payments { get; set; }
}

public class TandaPaymentDto
{
    public Guid Id { get; set; }
    public Guid ParticipantId { get; set; }
    public int WeekNumber { get; set; }
    public decimal AmountPaid { get; set; }
    public decimal PenaltyPaid { get; set; }
    public DateTime PaymentDate { get; set; }
    public bool IsVerified { get; set; }
    public string? Notes { get; set; }
}

public class TandaProductDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal BasePrice { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class UpdateTurnDto
{
    public int NewTurn { get; set; }
}

public class UpdateParticipantVariantDto
{
    public string? Variant { get; set; }
}

public class ReorderParticipantsDto
{
    public List<Guid> ParticipantIds { get; set; } = new();
}

public class UpdateTandaParticipantDto
{
    [Range(1, int.MaxValue)]
    public int CustomerId { get; set; }

    [Range(1, 52)]
    public int AssignedTurn { get; set; }

    [MaxLength(255)]
    public string? Variant { get; set; }

    [Range(typeof(decimal), "0.01", "9999999999")]
    public decimal? WeeklyAmount { get; set; }

    [MaxLength(10)]
    public string? Currency { get; set; }

    public decimal? ItemCost { get; set; }

    public decimal? ExchangeRate { get; set; }

    [RegularExpression("^(Active|Delinquent|Completed)$")]
    public string Status { get; set; } = "Active";

    public bool IsDelivered { get; set; }

    public DateTime? DeliveryDate { get; set; }
}

public class UpdateTandaPaymentDto
{
    [Range(1, 52)]
    public int WeekNumber { get; set; }

    [Range(typeof(decimal), "0.01", "9999999999")]
    public decimal AmountPaid { get; set; }

    [Range(typeof(decimal), "0", "9999999999")]
    public decimal PenaltyPaid { get; set; }

    public DateTime PaymentDate { get; set; }

    public bool IsVerified { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }
}

public class UpdateTandaPlacesDto
{
    [Required]
    public List<TandaPlaceAssignmentDto> Assignments { get; set; } = new();
}

public class TandaPlaceAssignmentDto
{
    [Required]
    public Guid ParticipantId { get; set; }

    [Range(1, 52)]
    public int AssignedTurn { get; set; }
}
