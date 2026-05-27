using System.ComponentModel.DataAnnotations;

namespace EntregasApi.Models;

public enum LiveSessionStatus
{
    Queued = 0,
    Downloading = 1,
    Transcribing = 2,
    Parsing = 3,
    Scanning = 4,
    Ready = 5,
    Failed = 6,
}

public class LiveSession
{
    public int Id { get; set; }

    [Required, MaxLength(500)]
    public string FacebookUrl { get; set; } = "";

    [MaxLength(200)]
    public string? Title { get; set; }

    [MaxLength(500)]
    public string? R2Key { get; set; }

    public LiveSessionStatus Status { get; set; } = LiveSessionStatus.Queued;

    [MaxLength(500)]
    public string? StatusDetail { get; set; }

    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
    public double? DurationSeconds { get; set; }

    // Ruta local en disco (típicamente /tmp/) del audio descargado.
    // Se mantiene tras procesar para poder extraer clips de cada candidato.
    [MaxLength(500)]
    public string? LocalAudioPath { get; set; }

    public ICollection<LiveProduct> Products { get; set; } = new List<LiveProduct>();
    public ICollection<LiveCandidate> Candidates { get; set; } = new List<LiveCandidate>();
}
