using System.ComponentModel.DataAnnotations;

namespace EntregasApi.Models;

public enum ClientMergeMode { Manual = 0, Auto = 1 }

public class ClientMergeAudit
{
    public int Id { get; set; }
    public int SourceClientId { get; set; }
    [Required, MaxLength(200)] public string SourceName { get; set; } = "";
    public int TargetClientId { get; set; }
    [Required, MaxLength(200)] public string TargetName { get; set; } = "";
    public ClientMergeMode Mode { get; set; }
    [MaxLength(300)] public string? Reason { get; set; }    // "auto: same phone + name 0.99"
    public double Confidence { get; set; }                  // similarity score 0-1
    public int OrdersMoved { get; set; }
    public int AliasesMoved { get; set; }
    public DateTime MergedAt { get; set; } = DateTime.UtcNow;
}
