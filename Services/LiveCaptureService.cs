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
    private static readonly string[] CommonYtDlpPaths = { "yt-dlp", "/usr/local/bin/yt-dlp", "/usr/bin/yt-dlp" };
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
        var normalizedUrl = NormalizeLiveUrl(facebookUrl);
        EnsureSupportedLiveUrl(normalizedUrl);

        var session = new LiveSession
        {
            FacebookUrl = normalizedUrl,
            Title = title,
            Status = LiveSessionStatus.Queued,
            StatusDetail = "En cola. En unos segundos empieza la descarga.",
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
            session.StatusDetail = "Descargando audio del live con yt-dlp...";
            await db.SaveChangesAsync();

            var audioFilePath = await DownloadAudioAsync(session.Id, session.FacebookUrl);
            if (string.IsNullOrEmpty(audioFilePath) || !File.Exists(audioFilePath))
                throw new InvalidOperationException("yt-dlp no genero un archivo de audio para este live.");

            var audioInfo = new FileInfo(audioFilePath);
            session.StatusDetail = $"Audio descargado ({FormatBytes(audioInfo.Length)}). Preparando transcripcion...";
            await db.SaveChangesAsync();

            // Persistimos el path del audio para poder recortar clips por candidato
            // después. Importante: NO borrar este archivo al terminar el procesamiento.
            session.LocalAudioPath = audioFilePath.Length > 500
                ? audioFilePath[..500]
                : audioFilePath;
            await db.SaveChangesAsync();

            // 2. Transcribe
            session.Status = LiveSessionStatus.Transcribing;
            session.StatusDetail = "Transcribiendo audio con Whisper...";
            await db.SaveChangesAsync();

            var chunkSeconds = _config.GetValue<int>("LiveCapture:AudioChunkSeconds", 600);
            var segments = await TranscribeInChunksAsync(audioFilePath, sessionId, chunkSeconds);
            var transcriptTextLength = segments.Sum(s => s.Text?.Length ?? 0);
            if (segments.Count == 0 || transcriptTextLength < 20)
                throw new InvalidOperationException("Whisper no devolvio texto suficiente para analizar el live.");

            // 3. Parse
            session.Status = LiveSessionStatus.Parsing;
            session.StatusDetail = $"Transcripcion lista: {segments.Count} segmentos. Detectando productos...";
            
            var timedLines = segments.Select(s =>
            {
                var ts = TimeSpan.FromSeconds(s.StartSeconds);
                return $"[{ts:hh\\:mm\\:ss}] {s.Text.Trim()}";
            });
            session.Transcript = string.Join("\n", timedLines);
            
            await db.SaveChangesAsync();

            var products = await DetectProductsAsync(gemini, segments, sessionId);
            db.LiveProducts.AddRange(products);
            await db.SaveChangesAsync();

            session.StatusDetail = $"Productos detectados: {products.Count}. Detectando pedidos leidos en voz alta...";
            await db.SaveChangesAsync();

            var spokenOrders = await DetectSpokenOrdersAsync(gemini, segments, sessionId, products);
            db.LiveSpokenOrders.AddRange(spokenOrders);
            await db.SaveChangesAsync();

            session.StatusDetail = $"Pedidos hablados detectados: {spokenOrders.Count}. Armando candidatos...";
            await db.SaveChangesAsync();

            // 4. Build candidates
            var candidateCount = await BuildCandidatesAsync(db, sessionId);

            // 5. Done
            session.Status = LiveSessionStatus.Ready;
            session.ProcessedAt = DateTime.UtcNow;
            session.StatusDetail = candidateCount > 0
                ? $"Listo: {products.Count} productos y {candidateCount} candidatos detectados."
                : $"Termino sin candidatos: {products.Count} productos detectados, {spokenOrders.Count} pedidos hablados. Revisa si el audio se entendio o si la duena no leyo pedidos en voz alta.";
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
        var normalizedUrl = NormalizeLiveUrl(url);

        EnsureSupportedLiveUrl(normalizedUrl);

        try
        {
            await RunYtDlpDownloadAsync(outputTemplate, normalizedUrl);

            // Find the downloaded file
            var files = Directory.GetFiles("/tmp", $"live_{sessionId}.*");
            return files.FirstOrDefault();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Download failed: {ex.Message}", ex);
        }
    }

    private static string NormalizeLiveUrl(string url)
    {
        var normalized = (url ?? string.Empty).Trim().Trim('"', '\'');

        if (normalized.StartsWith("hhttps://", StringComparison.OrdinalIgnoreCase))
            normalized = "https://" + normalized["hhttps://".Length..];

        if (normalized.StartsWith("hhttp://", StringComparison.OrdinalIgnoreCase))
            normalized = "http://" + normalized["hhttp://".Length..];

        if (normalized.StartsWith("ttps://", StringComparison.OrdinalIgnoreCase))
            normalized = "h" + normalized;

        if (normalized.StartsWith("ttp://", StringComparison.OrdinalIgnoreCase))
            normalized = "h" + normalized;

        return normalized;
    }

    private static void EnsureSupportedLiveUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("Pega un URL valido que empiece con https://");
        }

        var host = uri.Host.ToLowerInvariant();
        var supported =
            host == "fb.watch" ||
            host.EndsWith("facebook.com") ||
            host.EndsWith("youtube.com") ||
            host == "youtu.be";

        if (!supported)
            throw new ArgumentException("El URL debe ser de Facebook o YouTube.");
    }

    private async Task RunYtDlpDownloadAsync(string outputTemplate, string url)
    {
        var errors = new List<string>();
        var configuredPath = _config["LiveCapture:YtDlpPath"];
        var directPaths = CommonYtDlpPaths
            .Prepend(string.IsNullOrWhiteSpace(configuredPath) ? "yt-dlp" : configuredPath.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var fileName in directPaths)
        {
            try
            {
                await RunYtDlpProcessAsync(fileName, new[] { "-f", "bestaudio", "-o", outputTemplate, url }, fileName);
                return;
            }
            catch (Exception ex)
            {
                errors.Add($"{fileName}: {ex.Message}");
                _logger.LogWarning(ex, "No se pudo ejecutar yt-dlp usando {FileName}", fileName);
            }
        }

        var pythonPath = string.IsNullOrWhiteSpace(_config["LiveCapture:YtDlpPythonPath"])
            ? "python3"
            : _config["LiveCapture:YtDlpPythonPath"]!.Trim();

        foreach (var fileName in new[] { pythonPath, "python" }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                await RunYtDlpProcessAsync(
                    fileName,
                    new[] { "-m", "yt_dlp", "-f", "bestaudio", "-o", outputTemplate, url },
                    $"{fileName} -m yt_dlp");
                return;
            }
            catch (Exception ex)
            {
                errors.Add($"{fileName} -m yt_dlp: {ex.Message}");
                _logger.LogWarning(ex, "No se pudo ejecutar yt-dlp usando {FileName} -m yt_dlp", fileName);
            }
        }

        throw new InvalidOperationException(
            "No se pudo ejecutar yt-dlp. Verifica que Render este usando el Dockerfile del API o configura LiveCapture__YtDlpPath con la ruta absoluta del ejecutable. " +
            $"Intentos: {string.Join(" | ", errors)}");
    }

    private static async Task RunYtDlpProcessAsync(string fileName, IEnumerable<string> arguments, string displayName)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start {displayName}");

        var stderrTask = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        await process.WaitForExitAsync(cts.Token);

        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{displayName} exited with code {process.ExitCode}: {stderr}");
    }

    private async Task<List<TranscriptSegment>> WhisperTranscribeAsync(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            throw new InvalidOperationException($"No se encontro el audio descargado: {filePath}");
        }

        var apiKey = _config["OpenAI:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("Falta configurar OpenAI:ApiKey para transcribir el live.");
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
            throw new InvalidOperationException($"Whisper no pudo transcribir el audio: {ex.Message}", ex);
        }
    }

    private async Task<List<TranscriptSegment>> TranscribeInChunksAsync(string filePath, int sessionId, int chunkSeconds)
    {
        var allSegments = new List<TranscriptSegment>();
        var chunkPattern = $"/tmp/live_{sessionId}_chunk_%03d.mp3";

        // Limpiar fragmentos viejos si los hubiera
        foreach (var file in Directory.GetFiles("/tmp", $"live_{sessionId}_chunk_*"))
        {
            try { File.Delete(file); } catch { }
        }

        var psi = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(filePath);
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("segment");
        psi.ArgumentList.Add("-segment_time");
        psi.ArgumentList.Add(chunkSeconds.ToString());
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("libmp3lame");
        psi.ArgumentList.Add("-b:a");
        psi.ArgumentList.Add("64k");
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add("22050");
        psi.ArgumentList.Add(chunkPattern);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("No se pudo iniciar ffmpeg para segmentar el audio.");
        
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        await process.WaitForExitAsync(cts.Token);
        
        if (process.ExitCode != 0)
        {
            var err = await process.StandardError.ReadToEndAsync();
            _logger.LogError("ffmpeg segmenting failed. ExitCode={Code}, Stderr={Stderr}", process.ExitCode, err);
            throw new InvalidOperationException($"ffmpeg fallo al segmentar: {err}");
        }

        var chunkFiles = Directory.GetFiles("/tmp", $"live_{sessionId}_chunk_*").OrderBy(f => f).ToList();
        
        if (chunkFiles.Count == 0)
        {
            throw new InvalidOperationException("ffmpeg no genero ningun fragmento de audio.");
        }

        for (int i = 0; i < chunkFiles.Count; i++)
        {
            var chunkFile = chunkFiles[i];
            var timeOffset = i * chunkSeconds;

            var chunkSegments = await WhisperTranscribeAsync(chunkFile);

            var chunkText = string.Join(" ", chunkSegments.Select(s => s.Text?.Trim()).Where(t => !string.IsNullOrEmpty(t)));
            _logger.LogInformation("Fragmento {Index}/{Total} transcrito. Texto extraido: {Text}", i + 1, chunkFiles.Count, chunkText);

            foreach (var seg in chunkSegments)
            {
                var start = seg.StartSeconds + timeOffset;
                var end = seg.EndSeconds + timeOffset;
                allSegments.Add(new TranscriptSegment(start, end, seg.Text));
            }

            try { File.Delete(chunkFile); } catch { }
        }

        return allSegments;
    }

    private async Task<List<LiveProduct>> DetectProductsAsync(IGeminiService gemini, List<TranscriptSegment> segments, int sessionId)
    {
        if (segments.Count == 0)
            return new List<LiveProduct>();

        try
        {
            var timedLines = segments.Select(s =>
            {
                var ts = TimeSpan.FromSeconds(s.StartSeconds);
                return $"[{ts:hh\\:mm\\:ss}] {s.Text.Trim()}";
            });
            var timedTranscript = string.Join("\n", timedLines);

            var prompt = $@"Eres un asistente que analiza la transcripción timbrada de un Facebook Live de ventas en español.
Cada línea tiene el formato [HH:MM:SS] texto.
Extrae TODOS los productos que la dueña anuncia con su palabra clave y precio.
Para cada uno incluye el segundo exacto (en segundos desde el inicio) en que fue anunciado.
Responde SOLO con JSON array, sin Markdown:
[{{""keyword"":""botones"",""description"":""blusa de botones rosa"",""price"":120,""announcedAtSeconds"":45.0}}]

Transcripción timbrada:
{timedTranscript}";

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

                double? announcedAt = null;
                if (item.TryGetProperty("announcedAtSeconds", out var aat) && aat.ValueKind == JsonValueKind.Number)
                    announcedAt = aat.GetDouble();

                products.Add(new LiveProduct
                {
                    LiveSessionId = sessionId,
                    Keyword = keyword[..Math.Min(keyword.Length, 100)],
                    Description = item.TryGetProperty("description", out var desc)
                        ? (desc.GetString() ?? "")[..Math.Min((desc.GetString() ?? "").Length, 300)]
                        : null,
                    Price = price,
                    AnnouncedAtSeconds = announcedAt,
                });
            }

            return products;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DetectProductsAsync failed");
            throw new InvalidOperationException($"Gemini no pudo detectar productos: {ex.Message}", ex);
        }
    }

    private async Task<List<LiveSpokenOrder>> DetectSpokenOrdersAsync(
        IGeminiService gemini,
        List<TranscriptSegment> segments,
        int sessionId,
        List<LiveProduct> products)
    {
        if (segments.Count == 0 || products.Count == 0)
            return new List<LiveSpokenOrder>();

        try
        {
            // Build a timed transcript: each line prefixed with "[HH:MM:SS]" so Gemini
            // can return the exact second of each spoken assignment.
            var timedLines = segments.Select(s =>
            {
                var ts = TimeSpan.FromSeconds(s.StartSeconds);
                return $"[{ts:hh\\:mm\\:ss}] {s.Text.Trim()}";
            });
            var timedTranscript = string.Join("\n", timedLines);

            var keywords = string.Join(", ", products.Select(p => p.Keyword));
            var prompt = $@"Eres un asistente que analiza la transcripción timbrada de un Facebook Live de ventas en español.
Cada línea tiene el formato [HH:MM:SS] texto.
Extrae TODAS las asignaciones de pedidos hablados por la dueña (frases como 'botones para Lupe', 'Lupe se lleva botones', 'apúntame una de azul a Patricia', etc.).
Las keywords de los productos disponibles son: {keywords}.
Para cada asignación devuelve el segundo exacto (en segundos desde el inicio del video) en que la dueña lo dijo.
Responde SOLO con JSON array, sin Markdown:
[{{""keyword"":""botones"",""clientName"":""Lupe López"",""spokenAtSeconds"":3723.0}}]

Transcripción timbrada:
{timedTranscript}";

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

                double? spokenAt = null;
                if (item.TryGetProperty("spokenAtSeconds", out var sat) && sat.ValueKind == JsonValueKind.Number)
                    spokenAt = sat.GetDouble();

                orders.Add(new LiveSpokenOrder
                {
                    LiveSessionId = sessionId,
                    Keyword = keyword[..Math.Min(keyword.Length, 100)],
                    ClientNameSpoken = clientName[..Math.Min(clientName.Length, 200)],
                    SpokenAtSeconds = spokenAt,
                });
            }

            return orders;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DetectSpokenOrdersAsync failed");
            throw new InvalidOperationException($"Gemini no pudo detectar pedidos hablados: {ex.Message}", ex);
        }
    }

    private async Task<int> BuildCandidatesAsync(AppDbContext db, int sessionId)
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

        return await db.SaveChangesAsync();
    }

    // ── Helpers ──

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.0} KB";
        if (bytes < 1024L * 1024L * 1024L) return $"{bytes / 1024d / 1024d:0.0} MB";
        return $"{bytes / 1024d / 1024d / 1024d:0.0} GB";
    }

    private static LiveSessionDto MapSessionDto(LiveSession s, int productCount, int candidateCount, int pendingCount) =>
        new(s.Id, s.FacebookUrl, s.Title, s.Status.ToString(), s.StatusDetail,
            s.ImportedAt, s.ProcessedAt, s.DurationSeconds,
            productCount, candidateCount, pendingCount,
            s.Transcript);

    private static LiveCandidateDto MapCandidateDto(LiveCandidate c) =>
        new(c.Id, c.Keyword, c.LiveProductId,
            c.ClientNameSpoken, c.CommentDisplayName,
            c.ResolvedClientId, c.ResolvedClient?.Name,
            c.ProposedAliasPairJson,
            c.Source.ToString(), c.Status.ToString(),
            c.SpokenAtSeconds);
}
