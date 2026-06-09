using System.Collections.Concurrent;
using System.Text;

namespace EntregasApi.Services;

/// <summary>
/// Servicio de Text-to-Speech con ElevenLabs. Mantiene la misma interfaz que
/// <see cref="IGoogleTtsService"/> para que sea un drop-in replacement, con
/// cache en memoria por hash del texto (ahorra costos: un saludo idéntico
/// no se vuelve a sintetizar) y fallback automático a Google TTS si ElevenLabs
/// falla (rate limit, red, key inválida).
/// </summary>
public interface IElevenLabsTtsService
{
    Task<string> SynthesizeAsync(string text);
}

public class ElevenLabsTtsService : IElevenLabsTtsService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<ElevenLabsTtsService> _logger;
    private readonly IGoogleTtsService? _fallback;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    public ElevenLabsTtsService(
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<ElevenLabsTtsService> logger,
        IServiceProvider sp)
    {
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
        // Fallback opcional: si ElevenLabs está registrado, Google también
        // lo está, así que lo resolvemos en runtime.
        _fallback = (IGoogleTtsService?)sp.GetService(typeof(IGoogleTtsService));
    }

    public async Task<string> SynthesizeAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // Limpiar markdown / formato antes de mandar a ElevenLabs.
        var cleanText = text
            .Replace("**", "").Replace("*", "").Replace("_", "")
            .Replace("#", "").Replace("`", "");

        // Cache: misma entrada de texto (post-limpieza) => mismo audio.
        var key = ComputeKey(cleanText);
        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
        {
            return cached.Base64;
        }

        var apiKey = _config["ElevenLabs:ApiKey"];
        var voiceId = _config["ElevenLabs:VoiceId"];
        var modelId = _config["ElevenLabs:ModelId"] ?? "eleven_multilingual_v2";

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(voiceId))
        {
            _logger.LogWarning("ElevenLabs no configurado (ApiKey/VoiceId vacíos). Usando fallback Google TTS.");
            return await SynthesizeWithFallbackAsync(cleanText);
        }

        try
        {
            var client = _httpFactory.CreateClient("ElevenLabs");
            client.Timeout = TimeSpan.FromSeconds(15);
            var url = $"https://api.elevenlabs.io/v1/text-to-speech/{voiceId}";
            var body = new
            {
                text = cleanText,
                model_id = modelId,
                voice_settings = new
                {
                    stability = 0.5,
                    similarity_boost = 0.75,
                    style = 0.0,
                    use_speaker_boost = true
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("xi-api-key", apiKey);
            req.Headers.Add("Accept", "audio/mpeg");
            req.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var resp = await client.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync();
                _logger.LogWarning("ElevenLabs respondió {Status}: {Body}. Fallback a Google TTS.", (int)resp.StatusCode, errBody);
                return await SynthesizeWithFallbackAsync(cleanText);
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync();
            var base64 = Convert.ToBase64String(bytes);

            // Guardamos en cache.
            _cache[key] = new CacheEntry(base64, DateTime.UtcNow.Add(CacheTtl));
            // Limpiamos entradas expiradas lazily.
            PurgeExpired();

            return base64;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ElevenLabs falló. Fallback a Google TTS.");
            return await SynthesizeWithFallbackAsync(cleanText);
        }
    }

    private async Task<string> SynthesizeWithFallbackAsync(string cleanText)
    {
        if (_fallback == null) return string.Empty;
        try
        {
            return await _fallback.SynthesizeAsync(cleanText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallback Google TTS también falló. Devolviendo audio vacío.");
            return string.Empty;
        }
    }

    private static string ComputeKey(string text)
    {
        // Hash simple. No necesitamos seguridad criptográfica.
        unchecked
        {
            int hash = 17;
            foreach (var c in text) hash = hash * 31 + c;
            return hash.ToString("X") + "_" + text.Length;
        }
    }

    private void PurgeExpired()
    {
        if (_cache.Count < 50) return; // Sólo cuando crezca bastante.
        var now = DateTime.UtcNow;
        foreach (var kv in _cache)
        {
            if (kv.Value.ExpiresAt <= now) _cache.TryRemove(kv.Key, out _);
        }
    }

    private record CacheEntry(string Base64, DateTime ExpiresAt);
}
