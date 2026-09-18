using System.ComponentModel.DataAnnotations;

namespace EntregasApi.DTOs;

public class CreateTandaDto
{
    [Required]
    public Guid ProductId { get; set; }

    [Required, MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    [Required]
    public int TotalWeeks { get; set; }

    [Required]
    public decimal WeeklyAmount { get; set; }

    public decimal PenaltyAmount { get; set; } = 0;

    [Required]
    public DateTime StartDate { get; set; }

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

    public List<CreateTandaParticipantItemDto> Items { get; set; } = new();
}

public class CreateTandaParticipantItemDto
{
    public Guid? ProductId { get; set; }

    [Required, MaxLength(255)]
    public string ProductName { get; set; } = string.Empty;

    [Range(1, 100)]
    public int Quantity { get; set; } = 1;

    public decimal UnitPrice { get; set; }
    public decimal? WeeklyAmount { get; set; }
    public string? Variant { get; set; }
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

    public List<CreateTandaParticipantItemDto> Items { get; set; } = new();
}
public class RegisterPaymentDto
{
    [Required]
    public Guid ParticipantId { get; set; }

    [Required]
    public int WeekNumber { get; set; }

    [Required]
    public decimal AmountPaid { get; set; }

    public decimal PenaltyPaid { get; set; } = 0;

    public string? Notes { get; set; }
}

public class VerifyTandaPaymentDto
{
    public bool IsVerified { get; set; }
    public decimal? AmountPaid { get; set; }
    public DateTime? DepositDate { get; set; }
    public string? Notes { get; set; }
}

public class ReplaceTandaParticipantItemsDto
{
    public List<CreateTandaParticipantItemDto> Items { get; set; } = new();
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
    public TandaParticipantViewDto? Participant { get; set; }
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
    public string PublicToken { get; set; } = string.Empty;
    public List<TandaParticipantItemDto> Items { get; set; } = new();
    public List<TandaPaymentDto> Payments { get; set; } = new();
}

public class UpdateTandaDto
{
    [Required, MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    [Required]
    public int TotalWeeks { get; set; }

    [Required]
    public decimal WeeklyAmount { get; set; }

    public decimal PenaltyAmount { get; set; }

    [Required]
    public DateTime StartDate { get; set; }
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
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string? AccessToken { get; set; }
    public TandaProductDto? Product { get; set; }
    public List<TandaParticipantDto>? Participants { get; set; }
}

public class TandaParticipantDto
{
    public Guid Id { get; set; }
    public Guid TandaId { get; set; }
    public int CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public string PublicToken { get; set; } = string.Empty;
    public int AssignedTurn { get; set; }
    public decimal? WeeklyAmount { get; set; }
    public bool IsDelivered { get; set; }
    public DateTime? DeliveryDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Variant { get; set; }
    public List<TandaPaymentDto>? Payments { get; set; }
    public List<TandaParticipantItemDto>? Items { get; set; }
}

public class TandaParticipantItemDto
{
    public Guid Id { get; set; }
    public Guid ParticipantId { get; set; }
    public Guid? ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal? WeeklyAmount { get; set; }
    public decimal LineTotal { get; set; }
    public string? Variant { get; set; }
}

public class TandaPaymentDto
{
    public Guid Id { get; set; }
    public Guid ParticipantId { get; set; }
    public int WeekNumber { get; set; }
    public decimal AmountPaid { get; set; }
    public decimal PenaltyPaid { get; set; }
    public DateTime PaymentDate { get; set; }
    public DateTime? DepositDate { get; set; }
    public decimal? OcrAmount { get; set; }
    public string? ProofUrl { get; set; }
    public string? OcrText { get; set; }
    public decimal? OcrConfidence { get; set; }
    public string? PaymentMethod { get; set; }
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
