using System.ComponentModel.DataAnnotations;

namespace EntregasApi.DTOs;

public class CreateRaffleDto
{
    [Required, MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }

    [MaxLength(500)]
    public string? ImageUrl { get; set; }

    [MaxLength(500)]
    public string? SocialShareImageUrl { get; set; }

    [MaxLength(50)]
    public string AnimationType { get; set; } = "roulette";

    // Premio
    [MaxLength(50)]
    public string PrizeType { get; set; } = "product";

    public decimal? PrizeValue { get; set; }

    public Guid? PrizeProductId { get; set; }

    [MaxLength(500)]
    public string? PrizeDescription { get; set; }

    [MaxLength(50)]
    public string? PrizeCurrency { get; set; } = "MXN";

    // Reglas de elegibilidad
    public int RequiredPurchases { get; set; } = 1;

    [MaxLength(50)]
    public string EligibilityRule { get; set; } = "purchaseCount";

    public decimal? MinOrderTotal { get; set; }

    public decimal? MinLifetimeSpent { get; set; }

    public DateTime? DateRangeStart { get; set; }

    public DateTime? DateRangeEnd { get; set; }

    public int? MaxEntriesPerClient { get; set; }

    // Segmento
    [MaxLength(50)]
    public string ClientSegmentFilter { get; set; } = "all";

    public bool NewClientsOnly { get; set; }

    public bool FrequentClientsOnly { get; set; }

    public bool VipOnly { get; set; }

    public bool ExcludeBlacklisted { get; set; } = true;

    public string? ExcludedClientIds { get; set; }
    
    public string? PreselectedWinnerIds { get; set; }

    // Tanda
    public Guid? TandaId { get; set; }

    public bool ShuffleTandaTurns { get; set; }

    // Automatización
    [Required]
    public DateTime RaffleDate { get; set; }

    public int WinnerCount { get; set; } = 1;

    public bool AutoDraw { get; set; }

    public bool NotifyWinner { get; set; } = true;

    [MaxLength(50)]
    public string? NotificationChannel { get; set; } = "push";

    [MaxLength(500)]
    public string? WinnerMessageTemplate { get; set; }

    // Template social
    [MaxLength(50)]
    public string? SocialTemplate { get; set; } = "default";

    [MaxLength(20)]
    public string? SocialBgColor { get; set; }

    [MaxLength(20)]
    public string? SocialTextColor { get; set; }
}

public class UpdateRaffleDto
{
    [MaxLength(255)]
    public string? Name { get; set; }

    [MaxLength(2000)]
    public string? Description { get; set; }

    [MaxLength(500)]
    public string? ImageUrl { get; set; }

    [MaxLength(500)]
    public string? SocialShareImageUrl { get; set; }

    [MaxLength(50)]
    public string? AnimationType { get; set; }

    public string? PrizeType { get; set; }
    public decimal? PrizeValue { get; set; }
    public Guid? PrizeProductId { get; set; }
    public string? PrizeDescription { get; set; }
    public string? PrizeCurrency { get; set; }

    public int? RequiredPurchases { get; set; }
    public string? EligibilityRule { get; set; }
    public decimal? MinOrderTotal { get; set; }
    public decimal? MinLifetimeSpent { get; set; }
    public DateTime? DateRangeStart { get; set; }
    public DateTime? DateRangeEnd { get; set; }
    public int? MaxEntriesPerClient { get; set; }

    public string? ClientSegmentFilter { get; set; }
    public bool? NewClientsOnly { get; set; }
    public bool? FrequentClientsOnly { get; set; }
    public bool? VipOnly { get; set; }
    public bool? ExcludeBlacklisted { get; set; }
    public string? ExcludedClientIds { get; set; }
    public string? PreselectedWinnerIds { get; set; }

    public Guid? TandaId { get; set; }
    public bool? ShuffleTandaTurns { get; set; }

    public DateTime? RaffleDate { get; set; }
    public int? WinnerCount { get; set; }
    public bool? AutoDraw { get; set; }
    public bool? NotifyWinner { get; set; }
    public string? NotificationChannel { get; set; }
    public string? WinnerMessageTemplate { get; set; }

    public string? SocialTemplate { get; set; }
    public string? SocialBgColor { get; set; }
    public string? SocialTextColor { get; set; }

    [MaxLength(50)]
    public string? Status { get; set; }
}

public class RaffleDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public string? SocialShareImageUrl { get; set; }
    public string AnimationType { get; set; } = "roulette";

    // Premio
    public string PrizeType { get; set; } = "product";
    public decimal? PrizeValue { get; set; }
    public Guid? PrizeProductId { get; set; }
    public TandaProductDto? PrizeProduct { get; set; }
    public string? PrizeDescription { get; set; }
    public string? PrizeCurrency { get; set; }

    // Reglas
    public int RequiredPurchases { get; set; }
    public string EligibilityRule { get; set; } = "purchaseCount";
    public decimal? MinOrderTotal { get; set; }
    public decimal? MinLifetimeSpent { get; set; }
    public DateTime? DateRangeStart { get; set; }
    public DateTime? DateRangeEnd { get; set; }
    public int? MaxEntriesPerClient { get; set; }

    // Segmento
    public string ClientSegmentFilter { get; set; } = "all";
    public bool NewClientsOnly { get; set; }
    public bool FrequentClientsOnly { get; set; }
    public bool VipOnly { get; set; }
    public bool ExcludeBlacklisted { get; set; }
    public string? ExcludedClientIds { get; set; }
    public string? PreselectedWinnerIds { get; set; }

    // Tanda
    public Guid? TandaId { get; set; }
    public string? TandaName { get; set; }
    public bool ShuffleTandaTurns { get; set; }

    // Automatización
    public DateTime RaffleDate { get; set; }
    public int WinnerCount { get; set; } = 1;
    public bool AutoDraw { get; set; }
    public bool NotifyWinner { get; set; }
    public string NotificationChannel { get; set; } = "push";
    public string? WinnerMessageTemplate { get; set; }

    // Social
    public string SocialTemplate { get; set; } = "default";
    public string? SocialBgColor { get; set; }
    public string? SocialTextColor { get; set; }

    // Estado
    public string Status { get; set; } = "Draft";
    public int? WinnerId { get; set; }
    public ClientDto? Winner { get; set; }
    public DateTime? AnnouncedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int ParticipantCount { get; set; }
    public int EntryCount { get; set; }
}

public class RaffleDetailDto : RaffleDto
{
    public List<RaffleParticipantDto> Participants { get; set; } = new();
    public List<RaffleEntryDto> Entries { get; set; } = new();
    public List<RaffleDrawDto> Draws { get; set; } = new();
}

public class RaffleParticipantDto
{
    public Guid Id { get; set; }
    public Guid RaffleId { get; set; }
    public int ClientId { get; set; }
    public ClientDto Client { get; set; } = null!;
    public DateTime QualificationDate { get; set; }
    public int QualifyingOrders { get; set; }
    public decimal? QualifyingTotalSpent { get; set; }
    public int EntryCount { get; set; } = 1;
    public bool IsWinner { get; set; }
    public int? AssignedTandaTurn { get; set; }
    public int? PreviousTandaTurn { get; set; }
    public bool Notified { get; set; }
    public DateTime? NotifiedAt { get; set; }
    public string? NotificationChannelUsed { get; set; }
}

public class RaffleEntryDto
{
    public Guid Id { get; set; }
    public Guid RaffleId { get; set; }
    public int ClientId { get; set; }
    public ClientDto Client { get; set; } = null!;
    public int OrderId { get; set; }
    public OrderSummaryDto Order { get; set; } = null!;
    public DateTime EnteredAt { get; set; }
}

public class RaffleDrawDto
{
    public Guid Id { get; set; }
    public Guid RaffleId { get; set; }
    public DateTime DrawDate { get; set; }
    public int? WinnerId { get; set; }
    public ClientDto? Winner { get; set; }
    public string SelectionMethod { get; set; } = "random";
    public bool IsTandaShuffle { get; set; }
    public int? TandaTurnsReshuffled { get; set; }
    public string? Notes { get; set; }
}

public class SelectWinnerDto
{
    [MaxLength(50)]
    public string SelectionMethod { get; set; } = "random";

    public int? ManualWinnerClientId { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    public int? Count { get; set; }
}

public class RaffleEvaluationResultDto
{
    public Guid RaffleId { get; set; }
    public int TotalQualified { get; set; }
    public List<RaffleParticipantDto> QualifiedParticipants { get; set; } = new();
    public List<RaffleEntryDto> NewEntries { get; set; } = new();
}

public class RaffleSummaryDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public string PrizeType { get; set; } = "product";
    public decimal? PrizeValue { get; set; }
    public string? PrizeDescription { get; set; }
    public DateTime RaffleDate { get; set; }
    public string Status { get; set; } = "Draft";
    public string AnimationType { get; set; } = "roulette";
    public Guid? TandaId { get; set; }
    public string? TandaName { get; set; }
    public bool ShuffleTandaTurns { get; set; }
    public int ParticipantCount { get; set; }
    public int WinnerCount { get; set; }
    public int? WinnerId { get; set; }
    public string? WinnerName { get; set; }
    public List<string>? WinnerNames { get; set; }
    public DateTime? AnnouncedAt { get; set; }
}

public class TandaShuffleResultDto
{
    public Guid RaffleId { get; set; }
    public Guid TandaId { get; set; }
    public string TandaName { get; set; } = string.Empty;
    public int ParticipantsShuffled { get; set; }
    public List<TandaTurnAssignmentDto> TurnAssignments { get; set; } = new();
    public DateTime ShuffleDate { get; set; } = DateTime.UtcNow;
}

public class TandaTurnAssignmentDto
{
    public int ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public int PreviousTurn { get; set; }
    public int NewTurn { get; set; }
}
