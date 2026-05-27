using System.ComponentModel.DataAnnotations;

namespace EntregasApi.Models;

public enum ClientAliasSource
{
    Unknown = 0,
    ManualConfirm = 1,
    Merge = 2,
    Import = 3,
    LiveOcr = 4,
    LiveAudio = 5,
}

public class ClientAlias
{
    [Key]
    public int Id { get; set; }

    public int ClientId { get; set; }
    public Client? Client { get; set; }

    [Required, MaxLength(200)]
    public string Alias { get; set; } = string.Empty;

    [Required]
    public string NormalizedAlias { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ClientAliasSource Source { get; set; } = ClientAliasSource.ManualConfirm;

    public int TimesSeen { get; set; } = 1;
}
