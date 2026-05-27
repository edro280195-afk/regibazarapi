using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models;

public enum LiveSessionStatus
{
    Downloading = 0,
    Transcribing = 1,
    Parsing = 2,
    Scanning = 3,
    Combining = 4,
    Ready = 5,
    Failed = 6
}

public enum LiveCandidateStatus
{
    Pending = 0,
    Confirmed = 1,
    Ignored = 2
}

public enum LiveCandidateSource
{
    Spoken = 0,
    CommentOnly = 1,
    SpokenAndComment = 2
}

public class LiveSession
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(2000)]
    public string FacebookUrl { get; set; } = string.Empty;

    [MaxLength(300)]
    public string? R2Key { get; set; }

    [MaxLength(300)]
    public string? Title { get; set; }

    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;

    public LiveSessionStatus Status { get; set; } = LiveSessionStatus.Downloading;

    public double? DurationSeconds { get; set; }

    public DateTime? ProcessedAt { get; set; }

    [MaxLength(2000)]
    public string? ErrorMessage { get; set; }

    public ICollection<LiveTranscriptSegment> TranscriptSegments { get; set; } = new List<LiveTranscriptSegment>();
    public ICollection<LiveProduct> Products { get; set; } = new List<LiveProduct>();
    public ICollection<LiveSpokenOrder> SpokenOrders { get; set; } = new List<LiveSpokenOrder>();
    public ICollection<LiveComment> Comments { get; set; } = new List<LiveComment>();
    public ICollection<LiveCommentOrder> CommentOrders { get; set; } = new List<LiveCommentOrder>();
    public ICollection<LiveCandidate> Candidates { get; set; } = new List<LiveCandidate>();
}

public class LiveTranscriptSegment
{
    [Key]
    public int Id { get; set; }

    public int LiveSessionId { get; set; }
    public LiveSession? LiveSession { get; set; }

    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }

    [Required]
    public string Text { get; set; } = string.Empty;
}

public class LiveProduct
{
    [Key]
    public int Id { get; set; }

    public int LiveSessionId { get; set; }
    public LiveSession? LiveSession { get; set; }

    [Required, MaxLength(120)]
    public string Keyword { get; set; } = string.Empty;

    [Required, MaxLength(120)]
    public string NormalizedKeyword { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string Description { get; set; } = string.Empty;

    [Column(TypeName = "decimal(10,2)")]
    public decimal Price { get; set; }

    public double AnnouncedAtSeconds { get; set; }

    public double Confidence { get; set; } = 0.8;

    public ICollection<LiveCandidate> Candidates { get; set; } = new List<LiveCandidate>();
}

public class LiveSpokenOrder
{
    [Key]
    public int Id { get; set; }

    public int LiveSessionId { get; set; }
    public LiveSession? LiveSession { get; set; }

    [Required, MaxLength(120)]
    public string Keyword { get; set; } = string.Empty;

    [Required, MaxLength(120)]
    public string NormalizedKeyword { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string ClientNameSpoken { get; set; } = string.Empty;

    public double SpokenAtSeconds { get; set; }

    public double Confidence { get; set; } = 0.8;
}

public class LiveComment
{
    [Key]
    public int Id { get; set; }

    public int LiveSessionId { get; set; }
    public LiveSession? LiveSession { get; set; }

    [MaxLength(200)]
    public string? DisplayName { get; set; }

    [Required, MaxLength(1000)]
    public string CommentText { get; set; } = string.Empty;

    public double TimestampSeconds { get; set; }

    public double OcrConfidence { get; set; }

    public string? RawText { get; set; }
}

public class LiveCommentOrder
{
    [Key]
    public int Id { get; set; }

    public int LiveSessionId { get; set; }
    public LiveSession? LiveSession { get; set; }

    public int LiveCommentId { get; set; }
    public LiveComment? LiveComment { get; set; }

    [Required, MaxLength(120)]
    public string Keyword { get; set; } = string.Empty;

    [Required, MaxLength(120)]
    public string NormalizedKeyword { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? CommentDisplayName { get; set; }

    public double CommentedAtSeconds { get; set; }

    public double OcrConfidence { get; set; }
}

public class LiveCandidate
{
    [Key]
    public int Id { get; set; }

    public int LiveSessionId { get; set; }
    public LiveSession? LiveSession { get; set; }

    public int? LiveProductId { get; set; }
    public LiveProduct? LiveProduct { get; set; }

    [Required, MaxLength(120)]
    public string Keyword { get; set; } = string.Empty;

    [Required, MaxLength(120)]
    public string NormalizedKeyword { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? ClientNameSpoken { get; set; }

    [MaxLength(200)]
    public string? CommentDisplayName { get; set; }

    public int? ResolvedClientId { get; set; }
    public Client? ResolvedClient { get; set; }

    [MaxLength(200)]
    public string? ProposedAlias { get; set; }

    [MaxLength(200)]
    public string? ProposedCanonicalName { get; set; }

    public LiveCandidateSource Source { get; set; }

    public LiveCandidateStatus Status { get; set; } = LiveCandidateStatus.Pending;

    public double Confidence { get; set; }

    public double? SpokenAtSeconds { get; set; }

    public double? CommentedAtSeconds { get; set; }

    public int? CreatedOrderId { get; set; }
    public Order? CreatedOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedAt { get; set; }
}
