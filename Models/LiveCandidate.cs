using System.ComponentModel.DataAnnotations;

namespace EntregasApi.Models;

public enum LiveCandidateStatus
{
    Pending = 0,
    Confirmed = 1,
    Ignored = 2,
}

public enum LiveCandidateSource
{
    Spoken = 0,
    Comment = 1,
    SpokenAndComment = 2,
}

public class LiveCandidate
{
    public int Id { get; set; }

    public int LiveSessionId { get; set; }
    public LiveSession? LiveSession { get; set; }

    public int? LiveProductId { get; set; }
    public LiveProduct? LiveProduct { get; set; }

    [MaxLength(100)]
    public string Keyword { get; set; } = "";

    [MaxLength(200)]
    public string? ClientNameSpoken { get; set; }

    [MaxLength(200)]
    public string? CommentDisplayName { get; set; }

    public int? ResolvedClientId { get; set; }
    public Client? ResolvedClient { get; set; }

    // JSON blob: { spoken: "Lupe López", comment: "YG YG" }
    [MaxLength(400)]
    public string? ProposedAliasPairJson { get; set; }

    public LiveCandidateSource Source { get; set; }
    public LiveCandidateStatus Status { get; set; } = LiveCandidateStatus.Pending;

    public int? CreatedOrderId { get; set; }

    // Segundo aproximado del audio en el que se habló este pedido.
    // Permite extraer clips de 5 segundos para revisión rápida.
    public double? SpokenAtSeconds { get; set; }
}
