using EntregasApi.Data;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EntregasApi.Services;

public interface ILiveTimelineBuilder
{
    Task BuildAsync(int liveSessionId, CancellationToken cancellationToken);
}

public class LiveTimelineBuilder : ILiveTimelineBuilder
{
    private readonly AppDbContext _db;
    private readonly IClientResolverService _resolver;
    private readonly LiveCaptureOptions _options;

    public LiveTimelineBuilder(
        AppDbContext db,
        IClientResolverService resolver,
        IOptions<LiveCaptureOptions> options)
    {
        _db = db;
        _resolver = resolver;
        _options = options.Value;
    }

    public async Task BuildAsync(int liveSessionId, CancellationToken cancellationToken)
    {
        await _db.LiveCandidates
            .Where(c => c.LiveSessionId == liveSessionId)
            .ExecuteDeleteAsync(cancellationToken);

        var products = await _db.LiveProducts
            .Where(p => p.LiveSessionId == liveSessionId)
            .OrderBy(p => p.AnnouncedAtSeconds)
            .ToListAsync(cancellationToken);

        var spokenOrders = await _db.LiveSpokenOrders
            .Where(o => o.LiveSessionId == liveSessionId)
            .OrderBy(o => o.SpokenAtSeconds)
            .ToListAsync(cancellationToken);

        var commentOrders = await _db.LiveCommentOrders
            .Where(o => o.LiveSessionId == liveSessionId)
            .OrderBy(o => o.CommentedAtSeconds)
            .ToListAsync(cancellationToken);

        var candidates = new List<LiveCandidate>();

        foreach (var product in products)
        {
            var productSpoken = spokenOrders
                .Where(o => o.NormalizedKeyword == product.NormalizedKeyword)
                .ToList();

            var productComments = commentOrders
                .Where(o => o.NormalizedKeyword == product.NormalizedKeyword)
                .ToList();

            var usedCommentIds = new HashSet<int>();

            foreach (var spoken in productSpoken)
            {
                var comment = productComments
                    .Where(c => !usedCommentIds.Contains(c.Id))
                    .Select(c => new
                    {
                        Comment = c,
                        Distance = Math.Abs(c.CommentedAtSeconds - spoken.SpokenAtSeconds)
                    })
                    .Where(x => x.Distance <= _options.CrossMatchWindowSeconds)
                    .OrderBy(x => x.Distance)
                    .Select(x => x.Comment)
                    .FirstOrDefault();

                if (comment != null)
                {
                    usedCommentIds.Add(comment.Id);
                }

                candidates.Add(await CreateCandidateAsync(product, spoken, comment));
            }

            foreach (var comment in productComments.Where(c => !usedCommentIds.Contains(c.Id)))
            {
                candidates.Add(await CreateCandidateAsync(product, null, comment));
            }
        }

        _db.LiveCandidates.AddRange(candidates);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<LiveCandidate> CreateCandidateAsync(
        LiveProduct product,
        LiveSpokenOrder? spoken,
        LiveCommentOrder? comment)
    {
        var source = (spoken, comment) switch
        {
            (not null, not null) => LiveCandidateSource.SpokenAndComment,
            (not null, null) => LiveCandidateSource.Spoken,
            _ => LiveCandidateSource.CommentOnly
        };

        var clientNameSpoken = CleanName(spoken?.ClientNameSpoken);
        var commentDisplayName = CleanName(comment?.CommentDisplayName);
        var identityName = !string.IsNullOrWhiteSpace(clientNameSpoken)
            ? clientNameSpoken!
            : commentDisplayName ?? string.Empty;

        int? resolvedClientId = null;
        if (!string.IsNullOrWhiteSpace(identityName))
        {
            var resolution = await _resolver.ResolveAsync(identityName, null, null);
            if (resolution.SuggestedAction == "use" && resolution.Candidates.Count > 0)
            {
                resolvedClientId = resolution.Candidates[0].ClientId;
            }
        }

        var proposedAlias = default(string);
        var proposedCanonicalName = default(string);
        if (!string.IsNullOrWhiteSpace(clientNameSpoken) &&
            !string.IsNullOrWhiteSpace(commentDisplayName) &&
            TextNormalizer.NormalizeName(clientNameSpoken) != TextNormalizer.NormalizeName(commentDisplayName))
        {
            proposedAlias = commentDisplayName;
            proposedCanonicalName = clientNameSpoken;
        }

        return new LiveCandidate
        {
            LiveSessionId = product.LiveSessionId,
            LiveProductId = product.Id,
            Keyword = product.Keyword,
            NormalizedKeyword = product.NormalizedKeyword,
            ClientNameSpoken = clientNameSpoken,
            CommentDisplayName = commentDisplayName,
            ResolvedClientId = resolvedClientId,
            ProposedAlias = proposedAlias,
            ProposedCanonicalName = proposedCanonicalName,
            Source = source,
            Status = LiveCandidateStatus.Pending,
            Confidence = CalculateConfidence(source, spoken, comment),
            SpokenAtSeconds = spoken?.SpokenAtSeconds,
            CommentedAtSeconds = comment?.CommentedAtSeconds,
            CreatedAt = DateTime.UtcNow
        };
    }

    private static string? CleanName(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static double CalculateConfidence(LiveCandidateSource source, LiveSpokenOrder? spoken, LiveCommentOrder? comment)
    {
        return source switch
        {
            LiveCandidateSource.SpokenAndComment => 0.95,
            LiveCandidateSource.Spoken => Math.Clamp(spoken?.Confidence ?? 0.75, 0, 1),
            _ => Math.Clamp(comment?.OcrConfidence ?? 0.65, 0, 1)
        };
    }
}
