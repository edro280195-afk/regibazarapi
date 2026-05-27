using System.Globalization;
using System.Text.RegularExpressions;
using EntregasApi.Data;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tesseract;

namespace EntregasApi.Services;

public interface ILiveCaptureService
{
    Task ProcessAsync(int liveSessionId, CancellationToken cancellationToken);
    Task<LiveClipResult> GetClipAsync(int liveSessionId, double atSeconds, int durationSeconds, CancellationToken cancellationToken);
}

public record LiveClipResult(byte[] Content, string ContentType, string FileName);

public class LiveCaptureService : ILiveCaptureService
{
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly string[] IgnoredOcrFragments =
    {
        "me gusta", "comentar", "compartir", "enviar", "facebook", "live", "reels", "escribe un comentario"
    };

    private readonly AppDbContext _db;
    private readonly IExternalProcessRunner _processRunner;
    private readonly ILiveStorageService _storage;
    private readonly IOpenAiTranscriptionService _transcriptionService;
    private readonly IGeminiService _geminiService;
    private readonly ILiveTimelineBuilder _timelineBuilder;
    private readonly LiveCaptureOptions _options;
    private readonly ILogger<LiveCaptureService> _logger;

    public LiveCaptureService(
        AppDbContext db,
        IExternalProcessRunner processRunner,
        ILiveStorageService storage,
        IOpenAiTranscriptionService transcriptionService,
        IGeminiService geminiService,
        ILiveTimelineBuilder timelineBuilder,
        IOptions<LiveCaptureOptions> options,
        ILogger<LiveCaptureService> logger)
    {
        _db = db;
        _processRunner = processRunner;
        _storage = storage;
        _transcriptionService = transcriptionService;
        _geminiService = geminiService;
        _timelineBuilder = timelineBuilder;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ProcessAsync(int liveSessionId, CancellationToken cancellationToken)
    {
        var workDir = CreateWorkDirectory(liveSessionId);

        try
        {
            var session = await _db.LiveSessions.FirstOrDefaultAsync(s => s.Id == liveSessionId, cancellationToken)
                ?? throw new InvalidOperationException($"LiveSession {liveSessionId} no existe.");

            await UpdateStatusAsync(session, LiveSessionStatus.Downloading, cancellationToken);
            var videoPath = await DownloadVideoAsync(session, workDir, cancellationToken);
            session.DurationSeconds = await ProbeDurationAsync(videoPath, cancellationToken);
            session.R2Key = await _storage.UploadVideoAsync(session.Id, videoPath, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);

            await UpdateStatusAsync(session, LiveSessionStatus.Transcribing, cancellationToken);
            var audioChunks = await ExtractAudioChunksAsync(videoPath, workDir, cancellationToken);
            var transcriptSegments = await _transcriptionService.TranscribeChunksAsync(
                session.Id,
                audioChunks,
                _options.AudioChunkSeconds,
                cancellationToken);

            await ReplaceTranscriptAsync(session.Id, transcriptSegments, cancellationToken);

            await UpdateStatusAsync(session, LiveSessionStatus.Parsing, cancellationToken);
            await RunTranscriptDetectorsAsync(session.Id, transcriptSegments, cancellationToken);

            await UpdateStatusAsync(session, LiveSessionStatus.Scanning, cancellationToken);
            var framePaths = await ExtractFramesAsync(videoPath, workDir, cancellationToken);
            var comments = RunOcr(framePaths, session.Id);
            await ReplaceCommentsAsync(session.Id, comments, cancellationToken);
            await BuildCommentOrdersAsync(session.Id, cancellationToken);

            await UpdateStatusAsync(session, LiveSessionStatus.Combining, cancellationToken);
            await _timelineBuilder.BuildAsync(session.Id, cancellationToken);

            session.Status = LiveSessionStatus.Ready;
            session.ProcessedAt = DateTime.UtcNow;
            session.ErrorMessage = null;
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo el pipeline de live {LiveSessionId}", liveSessionId);
            await MarkFailedAsync(liveSessionId, ex.Message, cancellationToken);
            throw;
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    public async Task<LiveClipResult> GetClipAsync(int liveSessionId, double atSeconds, int durationSeconds, CancellationToken cancellationToken)
    {
        var session = await _db.LiveSessions.FirstOrDefaultAsync(s => s.Id == liveSessionId, cancellationToken)
            ?? throw new InvalidOperationException("Live no encontrado.");

        if (string.IsNullOrWhiteSpace(session.R2Key))
            throw new InvalidOperationException("El live no tiene video en R2 todavia.");

        var workDir = CreateWorkDirectory(liveSessionId);
        try
        {
            var sourcePath = await _storage.DownloadVideoToTempAsync(session.R2Key, workDir, cancellationToken);
            var outputPath = Path.Combine(workDir, $"clip_{Guid.NewGuid():N}.mp4");
            var start = Math.Max(0, atSeconds).ToString("0.###", CultureInfo.InvariantCulture);
            var duration = Math.Clamp(durationSeconds, 1, 30).ToString(CultureInfo.InvariantCulture);

            await _processRunner.RunAsync(_options.FfmpegPath, new[]
            {
                "-y",
                "-ss", start,
                "-i", sourcePath,
                "-t", duration,
                "-c", "copy",
                "-avoid_negative_ts", "make_zero",
                outputPath
            }, cancellationToken);

            var bytes = await File.ReadAllBytesAsync(outputPath, cancellationToken);
            return new LiveClipResult(bytes, "video/mp4", $"live_{liveSessionId}_{start}s.mp4");
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    private async Task<string> DownloadVideoAsync(LiveSession session, string workDir, CancellationToken cancellationToken)
    {
        var outputTemplate = Path.Combine(workDir, "live.%(ext)s");
        await _processRunner.RunAsync(_options.YtDlpPath, new[]
        {
            "--no-playlist",
            "-f", "best[ext=mp4]/best",
            "-o", outputTemplate,
            session.FacebookUrl
        }, cancellationToken);

        var downloaded = Directory.GetFiles(workDir, "live.*")
            .Where(p => !p.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        return downloaded ?? throw new InvalidOperationException("yt-dlp no genero ningun archivo de video.");
    }

    private async Task<double?> ProbeDurationAsync(string videoPath, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner.RunAsync(_options.FfprobePath, new[]
            {
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                videoPath
            }, cancellationToken);

            return double.TryParse(result.StandardOutput.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
                ? duration
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo leer la duracion del video {VideoPath}", videoPath);
            return null;
        }
    }

    private async Task<List<string>> ExtractAudioChunksAsync(string videoPath, string workDir, CancellationToken cancellationToken)
    {
        var audioDir = Path.Combine(workDir, "audio");
        Directory.CreateDirectory(audioDir);
        var outputPattern = Path.Combine(audioDir, "chunk_%03d.wav");

        await _processRunner.RunAsync(_options.FfmpegPath, new[]
        {
            "-y",
            "-i", videoPath,
            "-vn",
            "-ac", "1",
            "-ar", "16000",
            "-f", "segment",
            "-segment_time", _options.AudioChunkSeconds.ToString(CultureInfo.InvariantCulture),
            outputPattern
        }, cancellationToken);

        return Directory.GetFiles(audioDir, "chunk_*.wav")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<string>> ExtractFramesAsync(string videoPath, string workDir, CancellationToken cancellationToken)
    {
        var framesDir = Path.Combine(workDir, "frames");
        Directory.CreateDirectory(framesDir);
        var vf = $"fps=1/{Math.Max(1, _options.FrameEverySeconds)}";
        if (!string.IsNullOrWhiteSpace(_options.FrameCropFilter))
        {
            vf = $"{vf},{_options.FrameCropFilter}";
        }

        var outputPattern = Path.Combine(framesDir, "frame_%06d.jpg");
        await _processRunner.RunAsync(_options.FfmpegPath, new[]
        {
            "-y",
            "-i", videoPath,
            "-vf", vf,
            "-q:v", "3",
            outputPattern
        }, cancellationToken);

        return Directory.GetFiles(framesDir, "frame_*.jpg")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<LiveComment> RunOcr(IReadOnlyList<string> framePaths, int liveSessionId)
    {
        if (framePaths.Count == 0) return new List<LiveComment>();
        if (!Directory.Exists(_options.TesseractDataPath))
            throw new InvalidOperationException($"No existe TesseractDataPath: {_options.TesseractDataPath}");

        var comments = new List<LiveComment>();
        var lastSeenByText = new Dictionary<string, double>();

        using var engine = new TesseractEngine(_options.TesseractDataPath, _options.TesseractLanguage, EngineMode.Default);

        for (var i = 0; i < framePaths.Count; i++)
        {
            var timestamp = i * Math.Max(1, _options.FrameEverySeconds);
            using var pix = Pix.LoadFromFile(framePaths[i]);
            using var page = engine.Process(pix);
            var rawText = page.GetText() ?? string.Empty;
            var confidence = Math.Clamp(page.GetMeanConfidence(), 0, 1);

            foreach (var parsed in ParseOcrComments(rawText))
            {
                var key = $"{TextNormalizer.NormalizeName(parsed.DisplayName)}|{TextNormalizer.NormalizeName(parsed.CommentText)}";
                if (string.IsNullOrWhiteSpace(key.Replace("|", ""))) continue;

                if (lastSeenByText.TryGetValue(key, out var lastSeen) && timestamp - lastSeen < 8)
                    continue;

                lastSeenByText[key] = timestamp;
                comments.Add(new LiveComment
                {
                    LiveSessionId = liveSessionId,
                    DisplayName = parsed.DisplayName,
                    CommentText = parsed.CommentText,
                    TimestampSeconds = timestamp,
                    OcrConfidence = confidence,
                    RawText = rawText
                });
            }
        }

        return comments;
    }

    private async Task RunTranscriptDetectorsAsync(
        int liveSessionId,
        List<LiveTranscriptSegment> transcriptSegments,
        CancellationToken cancellationToken)
    {
        await _db.LiveProducts.Where(p => p.LiveSessionId == liveSessionId).ExecuteDeleteAsync(cancellationToken);
        await _db.LiveSpokenOrders.Where(o => o.LiveSessionId == liveSessionId).ExecuteDeleteAsync(cancellationToken);

        var productDetections = new List<LiveProductDetection>();
        var spokenDetections = new List<LiveSpokenOrderDetection>();

        foreach (var window in BuildTranscriptWindows(transcriptSegments))
        {
            cancellationToken.ThrowIfCancellationRequested();
            productDetections.AddRange(await _geminiService.DetectLiveProductsAsync(window));
            spokenDetections.AddRange(await _geminiService.DetectLiveSpokenOrdersAsync(window));
        }

        var products = productDetections
            .Where(p => !string.IsNullOrWhiteSpace(p.Keyword))
            .Select(p => new
            {
                Detection = p,
                NormalizedKeyword = TextNormalizer.NormalizeName(p.Keyword)
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.NormalizedKeyword))
            .GroupBy(x => x.NormalizedKeyword)
            .Select(g => g.OrderBy(x => x.Detection.AnnouncedAtSeconds).First())
            .Select(x => new LiveProduct
            {
                LiveSessionId = liveSessionId,
                Keyword = x.Detection.Keyword.Trim(),
                NormalizedKeyword = x.NormalizedKeyword,
                Description = string.IsNullOrWhiteSpace(x.Detection.Description)
                    ? x.Detection.Keyword.Trim()
                    : x.Detection.Description.Trim(),
                Price = Math.Max(0, x.Detection.Price),
                AnnouncedAtSeconds = Math.Max(0, x.Detection.AnnouncedAtSeconds),
                Confidence = Math.Clamp(x.Detection.Confidence, 0, 1)
            })
            .ToList();

        _db.LiveProducts.AddRange(products);
        await _db.SaveChangesAsync(cancellationToken);

        var catalog = products.ToDictionary(p => p.NormalizedKeyword, p => p);
        var spokenOrders = spokenDetections
            .Where(o => !string.IsNullOrWhiteSpace(o.Keyword) && !string.IsNullOrWhiteSpace(o.ClientNameSpoken))
            .Select(o => new
            {
                Detection = o,
                Product = FindProductForKeyword(o.Keyword, catalog)
            })
            .Where(x => x.Product != null)
            .GroupBy(x => new
            {
                x.Product!.NormalizedKeyword,
                Client = TextNormalizer.NormalizeName(x.Detection.ClientNameSpoken),
                Minute = (int)(Math.Max(0, x.Detection.SpokenAtSeconds) / 60)
            })
            .Select(g => g.OrderBy(x => x.Detection.SpokenAtSeconds).First())
            .Select(x => new LiveSpokenOrder
            {
                LiveSessionId = liveSessionId,
                Keyword = x.Product!.Keyword,
                NormalizedKeyword = x.Product.NormalizedKeyword,
                ClientNameSpoken = x.Detection.ClientNameSpoken.Trim(),
                SpokenAtSeconds = Math.Max(0, x.Detection.SpokenAtSeconds),
                Confidence = Math.Clamp(x.Detection.Confidence, 0, 1)
            })
            .ToList();

        _db.LiveSpokenOrders.AddRange(spokenOrders);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task BuildCommentOrdersAsync(int liveSessionId, CancellationToken cancellationToken)
    {
        await _db.LiveCommentOrders.Where(o => o.LiveSessionId == liveSessionId).ExecuteDeleteAsync(cancellationToken);

        var products = await _db.LiveProducts
            .Where(p => p.LiveSessionId == liveSessionId)
            .ToListAsync(cancellationToken);

        var comments = await _db.LiveComments
            .Where(c => c.LiveSessionId == liveSessionId)
            .ToListAsync(cancellationToken);

        var orders = new List<LiveCommentOrder>();

        foreach (var comment in comments)
        {
            var text = TextNormalizer.NormalizeName($"{comment.DisplayName} {comment.CommentText}");
            if (string.IsNullOrWhiteSpace(text)) continue;

            var match = products
                .Select(p => new
                {
                    Product = p,
                    Score = KeywordScore(p.NormalizedKeyword, text)
                })
                .Where(x => x.Score >= _options.KeywordMatchThreshold)
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();

            if (match == null) continue;

            orders.Add(new LiveCommentOrder
            {
                LiveSessionId = liveSessionId,
                LiveCommentId = comment.Id,
                Keyword = match.Product.Keyword,
                NormalizedKeyword = match.Product.NormalizedKeyword,
                CommentDisplayName = comment.DisplayName,
                CommentedAtSeconds = comment.TimestampSeconds,
                OcrConfidence = Math.Min(1, Math.Max(comment.OcrConfidence, match.Score * 0.9))
            });
        }

        _db.LiveCommentOrders.AddRange(orders);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task ReplaceTranscriptAsync(int liveSessionId, List<LiveTranscriptSegment> transcriptSegments, CancellationToken cancellationToken)
    {
        await _db.LiveTranscriptSegments
            .Where(s => s.LiveSessionId == liveSessionId)
            .ExecuteDeleteAsync(cancellationToken);

        _db.LiveTranscriptSegments.AddRange(transcriptSegments);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task ReplaceCommentsAsync(int liveSessionId, List<LiveComment> comments, CancellationToken cancellationToken)
    {
        await _db.LiveComments
            .Where(c => c.LiveSessionId == liveSessionId)
            .ExecuteDeleteAsync(cancellationToken);

        _db.LiveComments.AddRange(comments);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task UpdateStatusAsync(LiveSession session, LiveSessionStatus status, CancellationToken cancellationToken)
    {
        session.Status = status;
        session.ErrorMessage = null;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkFailedAsync(int liveSessionId, string errorMessage, CancellationToken cancellationToken)
    {
        try
        {
            var session = await _db.LiveSessions.FirstOrDefaultAsync(s => s.Id == liveSessionId, cancellationToken);
            if (session == null) return;

            session.Status = LiveSessionStatus.Failed;
            session.ErrorMessage = errorMessage.Length <= 2000 ? errorMessage : errorMessage[..2000];
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo marcar Failed para live {LiveSessionId}", liveSessionId);
        }
    }

    private IEnumerable<List<LiveTranscriptSegment>> BuildTranscriptWindows(List<LiveTranscriptSegment> segments)
    {
        if (segments.Count == 0) yield break;

        var maxEnd = segments.Max(s => s.EndSeconds);
        var step = Math.Max(30, _options.TranscriptWindowSeconds - _options.TranscriptOverlapSeconds);

        for (var start = 0d; start <= maxEnd; start += step)
        {
            var end = start + _options.TranscriptWindowSeconds;
            var window = segments
                .Where(s => s.StartSeconds < end && s.EndSeconds >= start)
                .OrderBy(s => s.StartSeconds)
                .ToList();

            if (window.Count > 0) yield return window;
        }
    }

    private static LiveProduct? FindProductForKeyword(string keyword, Dictionary<string, LiveProduct> catalog)
    {
        var normalized = TextNormalizer.NormalizeName(keyword);
        if (catalog.TryGetValue(normalized, out var exact)) return exact;

        return catalog.Values
            .Select(p => new { Product = p, Score = TrigramJaccard(p.NormalizedKeyword, normalized) })
            .Where(x => x.Score >= 0.62)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Product)
            .FirstOrDefault();
    }

    private static double KeywordScore(string normalizedKeyword, string normalizedText)
    {
        if (string.IsNullOrWhiteSpace(normalizedKeyword) || string.IsNullOrWhiteSpace(normalizedText))
            return 0;

        var words = normalizedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Contains(normalizedKeyword)) return 1;
        if (normalizedText.Contains($" {normalizedKeyword} ") ||
            normalizedText.StartsWith($"{normalizedKeyword} ") ||
            normalizedText.EndsWith($" {normalizedKeyword}"))
        {
            return 1;
        }

        if (normalizedKeyword.Length <= 2) return 0;

        return words
            .Select(w => TrigramJaccard(normalizedKeyword, w))
            .Append(TrigramJaccard(normalizedKeyword, normalizedText))
            .Max();
    }

    private static double TrigramJaccard(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0;
        var setA = Trigrams(a);
        var setB = Trigrams(b);
        if (setA.Count == 0 || setB.Count == 0) return 0;
        var intersect = setA.Intersect(setB).Count();
        var union = setA.Union(setB).Count();
        return union == 0 ? 0 : (double)intersect / union;
    }

    private static HashSet<string> Trigrams(string value)
    {
        var padded = $"  {value} ";
        var set = new HashSet<string>();
        for (var i = 0; i <= padded.Length - 3; i++)
        {
            set.Add(padded.Substring(i, 3));
        }
        return set;
    }

    private static IEnumerable<(string? DisplayName, string CommentText)> ParseOcrComments(string rawText)
    {
        var lines = rawText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CleanOcrLine)
            .Where(line => line.Length >= 3)
            .Where(line => !IgnoredOcrFragments.Any(fragment => TextNormalizer.NormalizeName(line).Contains(fragment)))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var colonIndex = line.IndexOf(':');
            if (colonIndex > 0 && colonIndex < line.Length - 1)
            {
                var displayName = line[..colonIndex].Trim();
                var commentText = line[(colonIndex + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(commentText))
                    yield return (displayName, commentText);
                continue;
            }

            var dashIndex = line.IndexOf(" - ", StringComparison.Ordinal);
            if (dashIndex > 0 && dashIndex < line.Length - 3)
            {
                var displayName = line[..dashIndex].Trim();
                var commentText = line[(dashIndex + 3)..].Trim();
                if (!string.IsNullOrWhiteSpace(commentText))
                    yield return (displayName, commentText);
                continue;
            }

            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) continue;

            var nameTokenCount = tokens.Length >= 3 ? 2 : 1;
            var name = string.Join(' ', tokens.Take(nameTokenCount));
            var comment = string.Join(' ', tokens.Skip(nameTokenCount));
            if (!string.IsNullOrWhiteSpace(comment))
                yield return (name, comment);
        }
    }

    private static string CleanOcrLine(string value)
    {
        var cleaned = value
            .Replace("|", " ")
            .Replace("•", " ")
            .Replace("·", " ")
            .Trim();

        return WhitespaceRegex.Replace(cleaned, " ");
    }
    private string CreateWorkDirectory(int liveSessionId)
    {
        var root = _options.TempDirectory;
        if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();

        var dir = Path.Combine(root, $"live_{liveSessionId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // La limpieza de temporales no debe ocultar el resultado del pipeline.
        }
    }
}
