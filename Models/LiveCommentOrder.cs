using System.ComponentModel.DataAnnotations;

namespace EntregasApi.Models;

public class LiveCommentOrder
{
    public int Id { get; set; }

    public int LiveSessionId { get; set; }
    public LiveSession? LiveSession { get; set; }

    [Required, MaxLength(100)]
    public string Keyword { get; set; } = "";

    [MaxLength(200)]
    public string CommentDisplayName { get; set; } = "";

    public double? CommentedAtSeconds { get; set; }
    public double OcrConfidence { get; set; }
}
