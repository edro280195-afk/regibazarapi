using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EntregasApi.Models;

[Table("raffles")]
public class Raffle
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(255)]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2000)]
    [Column("description")]
    public string? Description { get; set; }

    [MaxLength(500)]
    [Column("image_url")]
    public string? ImageUrl { get; set; }

    [MaxLength(500)]
    [Column("social_share_image_url")]
    public string? SocialShareImageUrl { get; set; }

    [MaxLength(50)]
    [Column("animation_type")]
    public string AnimationType { get; set; } = "roulette";

    // ── Premio ──
    [MaxLength(50)]
    [Column("prize_type")]
    public string PrizeType { get; set; } = "product"; // product, discount, freeShipping, cash, giftCard, custom

    [Column("prize_value", TypeName = "decimal(12, 2)")]
    public decimal? PrizeValue { get; set; }

    [Column("prize_product_id")]
    public Guid? PrizeProductId { get; set; }

    [ForeignKey(nameof(PrizeProductId))]
    public TandaProduct? PrizeProduct { get; set; }

    [MaxLength(500)]
    [Column("prize_description")]
    public string? PrizeDescription { get; set; }

    [MaxLength(50)]
    [Column("prize_currency")]
    public string? PrizeCurrency { get; set; } = "MXN";

    // ── Reglas de elegibilidad ──
    [Column("required_purchases")]
    public int RequiredPurchases { get; set; } = 1;

    [MaxLength(50)]
    [Column("eligibility_rule")]
    public string EligibilityRule { get; set; } = "purchaseCount"; // purchaseCount, minSpent, recentActivity, custom

    [Column("min_order_total", TypeName = "decimal(12, 2)")]
    public decimal? MinOrderTotal { get; set; }

    [Column("min_lifetime_spent", TypeName = "decimal(12, 2)")]
    public decimal? MinLifetimeSpent { get; set; }

    [Column("date_range_start")]
    public DateTime? DateRangeStart { get; set; }

    [Column("date_range_end")]
    public DateTime? DateRangeEnd { get; set; }

    [Column("max_entries_per_client")]
    public int? MaxEntriesPerClient { get; set; }

    // ── Filtro de segmento ──
    [MaxLength(50)]
    [Column("client_segment_filter")]
    public string ClientSegmentFilter { get; set; } = "all"; // all, new, frequent, vip, blacklist, tandaParticipant

    [Column("new_clients_only")]
    public bool NewClientsOnly { get; set; }

    [Column("frequent_clients_only")]
    public bool FrequentClientsOnly { get; set; }

    [Column("vip_only")]
    public bool VipOnly { get; set; }

    [Column("exclude_blacklisted")]
    public bool ExcludeBlacklisted { get; set; } = true;

    [MaxLength(2000)]
    [Column("excluded_client_ids")]
    public string? ExcludedClientIds { get; set; }

    // ── Clientas preseleccionadas (IDs separados por coma, uso interno) ──
    [MaxLength(500)]
    [Column("preselected_winner_ids")]
    public string? PreselectedWinnerIds { get; set; }

    // ── Vinculación con Tanda ──
    [Column("tanda_id")]
    public Guid? TandaId { get; set; }

    [ForeignKey(nameof(TandaId))]
    public Tanda? Tanda { get; set; }

    [Column("shuffle_tanda_turns")]
    public bool ShuffleTandaTurns { get; set; }

    // ── Automatización ──
    [Column("raffle_date")]
    public DateTime RaffleDate { get; set; }

    [Column("auto_draw")]
    public bool AutoDraw { get; set; }

    [Column("notify_winner")]
    public bool NotifyWinner { get; set; } = true;

    [MaxLength(50)]
    [Column("notification_channel")]
    public string NotificationChannel { get; set; } = "push"; // push, whatsapp, both

    [MaxLength(500)]
    [Column("winner_message_template")]
    public string? WinnerMessageTemplate { get; set; }

    // ── Template de imagen social ──
    [MaxLength(50)]
    [Column("social_template")]
    public string SocialTemplate { get; set; } = "default"; // default, winner, celebration, custom

    [Column("social_bg_color")]
    [MaxLength(20)]
    public string? SocialBgColor { get; set; } = "#ec4899";

    [Column("social_text_color")]
    [MaxLength(20)]
    public string? SocialTextColor { get; set; } = "#ffffff";

    // ── Estado ──
    [Required, MaxLength(50)]
    [Column("status")]
    public string Status { get; set; } = "Draft"; // Draft, Active, Completed, Cancelled

    [Column("winner_count")]
    public int WinnerCount { get; set; } = 1;

    [Column("winner_id")]
    public int? WinnerId { get; set; }

    [ForeignKey(nameof(WinnerId))]
    public Client? Winner { get; set; }

    [Column("announced_at")]
    public DateTime? AnnouncedAt { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Relaciones
    public ICollection<RaffleParticipant> Participants { get; set; } = new List<RaffleParticipant>();
    public ICollection<RaffleEntry> Entries { get; set; } = new List<RaffleEntry>();
    public ICollection<RaffleDraw> Draws { get; set; } = new List<RaffleDraw>();
}
