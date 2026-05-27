using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public record TranscriptSegment(double StartSeconds, double EndSeconds, string Text);

public class LiveCaptureService : ILiveCaptureService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<LiveCaptureService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public LiveCaptureService(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<LiveCaptureService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    // ── Public interface ──

    public async Task<LiveSession> ImportAsync(string facebookUrl, string? title)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var session = new LiveSession
        {
            FacebookUrl = facebookUrl,
            Title = title,
            Status = LiveSessionStatus.Queued,
            ImportedAt = DateTime.UtcNow,
        };

        db.LiveSessions.Add(session);
        await db.SaveChangesAsync();

        var sessionId = session.Id;
        _ = Task.Run(async () =>
        {
            try
            {
                await ProcessAsync(sessionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in ProcessAsync for session {Id}", sessionId);
            }
        });

        return session;
    }

    public async Task<List<LiveSession>> GetAllAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.LiveSessions
            .OrderByDescending(s => s.ImportedAt)
            .ToListAsync();
    }

    public async Task<LiveSession?> GetByIdAsync(int id)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.LiveSessions.FindAsync(id);
    }

    public async Task<LiveReviewDto?> GetReviewAsync(int sessionId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var session = await db.LiveSessions
            .Include(s => s.Products)
            .Include(s => s.Candidates)
                .ThenInclude(c => c.ResolvedClient)
            .FirstOrDefaultAsync(s => s.Id == sessionId);

        if (session == null) return null;

        var products = session.Products.ToList();
        var candidates = session.Candidates.ToList();

        var productDtos = products.Select(p => new LiveProductDto(
            p.Id,
            p.Keyword,
            p.Description,
            p.Price,
            p.AnnouncedAtSeconds,
            candidates.Count(c => c.LiveProductId == p.Id)
        )).ToList();

        var candidatesByProduct = products.ToDictionary(
            p => p.Id,
            p => candidates
                .Where(c => c.LiveProductId == p.Id)
                .Select(c => MapCandidateDto(c))
                .ToList()
        );

        var unmatched = candidates
            .Where(c => c.LiveProductId == null)
            .Select(c => MapCandidateDto(c))
            .ToList();

        var sessionDto = MapSessionDto(session, products.Count, candidates.Count,
            candidates.Count(c => c.Status == LiveCandidateStatus.Pending));

        return new LiveReviewDto(sessionDto, productDtos, candidatesByProduct, unmatched);
    }

    public async Task ConfirmCandidateAsync(int candidateId, ConfirmCandidateRequest req)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var candidate = await db.LiveCandidates
            .Include(c => c.LiveProduct)
            .FirstOrDefaultAsync(c => c.Id == candidateId);

        if (candidate == null) throw new InvalidOperationException("Candidate not found");

        // Resolve or create client
        int clientId;
        var clientResolver = scope.ServiceProvider.GetRequiredService<IClientResolverService>();

        if (req.ClientId.HasValue)
        {
            clientId = req.ClientId.Value;
        }
        else if (!string.IsNullOrWhiteSpace(req.ClientName))
        {
            var resolved = await clientResolver.ResolveAsync(req.ClientName, null, null);
            if (resolved.SuggestedAction == "use" && resolved.Candidates.Count > 0)
            {
                clientId = resolved.Candidates[0].ClientId;
            }
            else
            {
                // Create new client
                var newClient = new Client
                {
                    Name = req.ClientName,
                    NormalizedName = TextNormalizer.NormalizeName(req.ClientName),
                    CreatedAt = DateTime.UtcNow,
                    Type = "Nueva",
                };
                db.Clients.Add(newClient);
                await db.SaveChangesAsync();
                clientId = newClient.Id;
            }
        }
        else
        {
            throw new ArgumentException("ClientId or ClientName is required");
        }

        candidate.ResolvedClientId = clientId;

        // Determine product info
        var productName = req.ProductOverride
            ?? (candidate.LiveProduct != null
                ? $"{candidate.LiveProduct.Keyword} {candidate.LiveProduct.Description}".Trim()
                : candidate.Keyword);
        var price = req.PriceOverride
            ?? candidate.LiveProduct?.Price
            ?? 0m;

        // Create order directly in DB
        var clientEntity = await db.Clients.FindAsync(clientId);
        var orderService = scope.ServiceProvider.GetRequiredService<IOrderService>();
        var dates = orderService.CalculateOrderDates(clientEntity?.Type ?? "Nueva", DateTime.UtcNow);

        var order = new Order
        {
            ClientId = clientId,
            Status = OrderStatus.Pending,
            OrderType = OrderType.Delivery,
            Subtotal = price,
            ShippingCost = 0m,
            Total = price,
            AccessToken = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = dates.ExpiresAt,
            ScheduledDeliveryDate = dates.ScheduledDeliveryDate,
        };

        order.Items.Add(new OrderItem
        {
            ProductName = productName,
            Quantity = 1,
            UnitPrice = price,
            LineTotal = price,
        });

        db.Orders.Add(order);
        await db.SaveChangesAsync();

        candidate.Status = LiveCandidateStatus.Confirmed;
        candidate.CreatedOrderId = order.Id;

        // Accept alias if requested
        if (req.AcceptAlias
            && !string.IsNullOrWhiteSpace(candidate.ClientNameSpoken)
            && !string.IsNullOrWhiteSpace(candidate.CommentDisplayName))
        {
            try
            {
                await clientResolver.AddAliasAsync(clientId, candidate.ClientNameSpoken, ClientAliasSource.LiveAudio);
                await clientResolver.AddAliasAsync(clientId, candidate.CommentDisplayName, ClientAliasSource.LiveOcr);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not add aliases for candidate {Id}", candidateId);
            }
        }

        await db.SaveChangesAsync();
    }

    public async Task IgnoreCandidateAsync(int candidateId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var candidate = await db.LiveCandidates.FindAsync(candidateId);
        if (candidate == null) throw new InvalidOperationException("Candidate not found");

        candidate.Status = LiveCandidateStatus.Ignored;
        await db.SaveChangesAsync();
    }

    public async Task<(Stream? stream, string? contentType)> GetCandidateClipAsync(int candidateId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var candidate = await db.LiveCandidates
            .Include(c => c.LiveSession)
            .FirstOrDefaultAsync(c => c.Id == candidateId);

        if (candidate == null) return (null, null);
        if (candidate.SpokenAtSeconds is not double spokenAt) return (null, null);

        var audioPath = candidate.LiveSession?.LocalAudioPath;
        if (string.IsNullOrEmpty(audioPath) || !File.Exists(audioPath)) return (null, null);

        // Empezamos 2 segundos antes del momento detectado para dar contexto
        // y extraemos 5 segundos en total.
        var startSeconds = Math.Max(0, spokenAt - 2);
        const int clipDurationSeconds = 5;

        var startArg = startSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        var psi = new ProcessStartInfo("ffmpeg")
        {
            Arguments = $"-ss {startArg} -t {clipDurationSeconds} -i \"{audioPath}\" -f mp3 -acodec libmp3lame -b:a 64k -ac 1 -ar 22050 -",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null) return (null, null);

            var memoryStream = new MemoryStream();
            var copyTask = process.StandardOutput.BaseStream.CopyToAsync(memoryStream);

            // Drenar stderr para evitar bloqueo por buffer lleno
            var errorTask = process.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { /* ignore */ }
                _logger.LogWarning("ffmpeg timed out generating clip for candidate {Id}", candidateId);
                memoryStream.Dispose();
                return (null, null);
            }

            await copyTask;
            await errorTask;

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("ffmpeg exited with code {Code} for candidate {Id}: {Err}",
                    process.ExitCode, candidateId, errorTask.Result);
                memoryStream.Dispose();
                return (null, null);
            }

            if (memoryStream.Length == 0)
            {
                memoryStream.Dispose();
                return (null, null);
            }

            memoryStream.Position = 0;
            return (memoryStream, "audio/mpeg");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract clip for candidate {Id}", candidateId);
            return (null, null);
        }
    }

    // ── Processing pipeline ──

    private async Task ProcessAsync(int sessionId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var gemini = scope.ServiceProvider.GetRequiredService<IGeminiService>();

        var session = await db.LiveSessions.FindAsync(sessionId);
        if (session == null) return;

        try
        {
            // 1. Download
            session.Status = LiveSessionStatus.Downloading;
            await db.SaveChangesAsync();

            var audioFilePath = await DownloadAudioAsync(session.Id, session.FacebookUrl);

            // Persistimos el path del audio para poder recortar clips por candidato
            // después. Importante: NO borrar este archivo al terminar el procesamiento.
            if (!string.IsNullOrEmpty(audioFilePath) && File.Exists(audioFilePath))
            {
                session.LocalAudioPath = audioFilePath.Length > 500
                    ? audioFilePath[..500]
                    : audioFilePath;
                await db.SaveChangesAsync();
            }

            // 2. Transcribe
            session.Status = LiveSessionStatus.Transcribing;
            await db.SaveChangesAsync();

            var segments = await WhisperTranscribeAsync(audioFilePath);

            // 3. Parse
            session.Status = LiveSessionStatus.Parsing;
            await db.SaveChangesAsync();

            var fullText = string.Join(" ", segments.Select(s => s.Text));

            var products = await DetectProductsAsync(gemini, fullText, sessionId);
            db.LiveProducts.AddRange(products);
            await db.SaveChangesAsync();

            var spokenOrders = await DetectSpokenOrdersAsync(gemini, fullText, sessionId, products);
            db.LiveSpokenOrders.AddRange(spokenOrders);
            await db.SaveChangesAsync();

            // 4. Build candidates
            await BuildCandidatesAsync(db, sessionId);

            // 5. Done
            session.Status = LiveSessionStatus.Ready;
            session.ProcessedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            _logger.LogInformation("LiveSession {Id} processed successfully", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Processing failed for LiveSession {Id}", sessionId);
            session.Status = LiveSessionStatus.Failed;
            session.StatusDetail = ex.Message.Length > 490 ? ex.Message[..490] : ex.Message;
            await db.SaveChangesAsync();
        }
    }

    private async Task<string?> DownloadAudioAsync(int sessionId, string url)
    {
        var outputTemplate = $"/tmp/live_{sessionId}.%(ext)s";

        // Only try yt-dlp for supported platforms
        if (!url.Contains("youtube") && !url.Contains("youtu.be") && !url.Contains("facebook.com"))
        {
            _logger.LogWarning("URL {Url} is not a supported platform; skipping download", url);
            return null;
        }

        try
        {
            var psi = new ProcessStartInfo("yt-dlp")
            {
                Arguments = $"-f bestaudio -o \"{outputTemplate}\" {url}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Could not start yt-dlp");

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            await process.WaitForExitAsync(cts.Token);

            if (process.ExitCode != 0)
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException($"yt-dlp exited with code {process.ExitCode}: {stderr}");
            }

            // Find the downloaded file
            var files = Directory.GetFiles("/tmp", $"live_{sessionId}.*");
            return files.FirstOrDefault();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Download failed: {ex.Message}", ex);
        }
    }

    private async Task<List<TranscriptSegment>> WhisperTranscribeAsync(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            _logger.LogWarning("Audio file not found: {Path}", filePath);
            return new List<TranscriptSegment> { new(0, 0, "") };
        }

        var apiKey = _config["OpenAI:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("OpenAI:ApiKey not configured; skipping transcription");
            return new List<TranscriptSegment> { new(0, 0, "") };
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var form = new MultipartFormDataContent();
            await using var fileStream = File.OpenRead(filePath);
            var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
            form.Add(fileContent, "file", Path.GetFileName(filePath));
            form.Add(new StringContent("whisper-1"), "model");
            form.Add(new StringContent("verbose_json"), "response_format");

            var response = await client.PostAsync("https://api.openai.com/v1/audio/transcriptions", form);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            var segments = new List<TranscriptSegment>();
            if (doc.RootElement.TryGetProperty("segments", out var segsEl))
            {
                foreach (var seg in segsEl.EnumerateArray())
                {
                    var start = seg.TryGetProperty("start", out var s) ? s.GetDouble() : 0;
                    var end = seg.TryGetProperty("end", out var e) ? e.GetDouble() : 0;
                    var text = seg.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    segments.Add(new TranscriptSegment(start, end, text));
                }
            }
            else if (doc.RootElement.TryGetProperty("text", out var textEl))
            {
                segments.Add(new TranscriptSegment(0, 0, textEl.GetString() ?? ""));
            }

            return segments.Count > 0 ? segments : new List<TranscriptSegment> { new(0, 0, "") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Whisper transcription failed");
            return new List<TranscriptSegment> { new(0, 0, "") };
        }
    }

    private async Task<List<LiveProduct>> DetectProductsAsync(IGeminiService gemini, string fullText, int sessionId)
    {
        if (string.IsNullOrWhiteSpace(fullText))
            return new List<LiveProduct>();

        try
        {
            var prompt = $@"Eres un asistente que analiza la transcripción de un Facebook Live de ventas en español. Extrae TODOS los productos que la dueña anuncia, cada uno con su PALABRA CLAVE y PRECIO. Formato: responde solo con JSON array: [{{""keyword"":""botones"",""description"":""blusa de botones rosa"",""price"":120}}]. Solo JSON, sin Markdown.

Transcripción:
{fullText}";

            var json = await gemini.CallGeminiJsonAsync(prompt);

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var raw = JsonSerializer.Deserialize<List<JsonElement>>(json, options);

            if (raw == null || raw.Count == 0)
                return new List<LiveProduct>();

            var products = new List<LiveProduct>();
            foreach (var item in raw)
            {
                var keyword = item.TryGetProperty("keyword", out var kw) ? kw.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(keyword)) continue;

                decimal price = 0;
                if (item.TryGetProperty("price", out var pr))
                {
                    if (pr.ValueKind == JsonValueKind.Number)
                        price = pr.GetDecimal();
                    else if (decimal.TryParse(pr.GetString(), out var p))
                        price = p;
                }

                products.Add(new LiveProduct
                {
                    LiveSessionId = sessionId,
                    Keyword = keyword[..Math.Min(keyword.Length, 100)],
                    Description = item.TryGetProperty("description", out var desc)
                        ? (desc.GetString() ?? "")[..Math.Min((desc.GetString() ?? "").Length, 300)]
                        : null,
                    Price = price,
                });
            }

            return products;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DetectProductsAsync failed");
            return new List<LiveProduct>();
        }
    }

    private async Task<List<LiveSpokenOrder>> DetectSpokenOrdersAsync(
        IGeminiService gemini,
        string fullText,
        int sessionId,
        List<LiveProduct> products)
    {
        if (string.IsNullOrWhiteSpace(fullText) || products.Count == 0)
            return new List<LiveSpokenOrder>();

        try
        {
            var keywords = string.Join(", ", products.Select(p => p.Keyword));
            var prompt = $@"De esta transcripción de un Facebook Live de ventas, extrae TODAS las asignaciones de pedidos hablados por la dueña (frases como 'botones para Lupe', 'Lupe se lleva botones', etc.). Las keywords disponibles son: {keywords}. Responde solo JSON: [{{""keyword"":""botones"",""clientName"":""Lupe López""}}]. Solo JSON, sin Markdown.

Transcripción:
{fullText}";

            var json = await gemini.CallGeminiJsonAsync(prompt);

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var raw = JsonSerializer.Deserialize<List<JsonElement>>(json, options);

            if (raw == null || raw.Count == 0)
                return new List<LiveSpokenOrder>();

            var orders = new List<LiveSpokenOrder>();
            foreach (var item in raw)
            {
                var keyword = item.TryGetProperty("keyword", out var kw) ? kw.GetString() ?? "" : "";
                var clientName = item.TryGetProperty("clientName", out var cn) ? cn.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(keyword) || string.IsNullOrWhiteSpace(clientName)) continue;

                orders.Add(new LiveSpokenOrder
                {
                    LiveSessionId = sessionId,
                    Keyword = keyword[..Math.Min(keyword.Length, 100)],
                    ClientNameSpoken = clientName[..Math.Min(clientName.Length, 200)],
                });
            }

            return orders;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DetectSpokenOrdersAsync failed");
            return new List<LiveSpokenOrder>();
        }
    }

    private async Task BuildCandidatesAsync(AppDbContext db, int sessionId)
    {
        var products = await db.LiveProducts
            .Where(p => p.LiveSessionId == sessionId)
            .ToListAsync();

        var spokenOrders = await db.LiveSpokenOrders
            .Where(o => o.LiveSessionId == sessionId)
            .ToListAsync();

        // Group by (keyword, clientName), match to products
        var groups = spokenOrders
            .GroupBy(o => new { Keyword = o.Keyword.ToLowerInvariant(), o.ClientNameSpoken })
            .ToList();

        foreach (var group in groups)
        {
            var matchedProduct = products.FirstOrDefault(p =>
                p.Keyword.Equals(group.Key.Keyword, StringComparison.OrdinalIgnoreCase));

            // Tomar el primer timestamp disponible dentro de los pedidos hablados
            // que componen este grupo. Sirve para que el frontend pueda reproducir
            // un clip de 5 segundos centrado en el momento en que se dijo el pedido.
            var spokenAtSeconds = group
                .Select(o => o.SpokenAtSeconds)
                .FirstOrDefault(s => s.HasValue);

            var candidate = new LiveCandidate
            {
                LiveSessionId = sessionId,
                LiveProductId = matchedProduct?.Id,
                Keyword = group.Key.Keyword[..Math.Min(group.Key.Keyword.Length, 100)],
                ClientNameSpoken = group.Key.ClientNameSpoken[..Math.Min(group.Key.ClientNameSpoken.Length, 200)],
                Source = LiveCandidateSource.Spoken,
                Status = LiveCandidateStatus.Pending,
                SpokenAtSeconds = spokenAtSeconds,
            };

            db.LiveCandidates.Add(candidate);
        }

        await db.SaveChangesAsync();
    }

    // ── Helpers ──

    private static LiveSessionDto MapSessionDto(LiveSession s, int productCount, int candidateCount, int pendingCount) =>
        new(s.Id, s.FacebookUrl, s.Title, s.Status.ToString(), s.StatusDetail,
            s.ImportedAt, s.ProcessedAt, s.DurationSeconds,
            productCount, candidateCount, pendingCount);

    private static LiveCandidateDto MapCandidateDto(LiveCandidate c) =>
        new(c.Id, c.Keyword, c.LiveProductId,
            c.ClientNameSpoken, c.CommentDisplayName,
            c.ResolvedClientId, c.ResolvedClient?.Name,
            c.ProposedAliasPairJson,
            c.Source.ToString(), c.Status.ToString(),
            c.SpokenAtSeconds);
}
