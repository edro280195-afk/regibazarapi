using Google.Cloud.TextToSpeech.V1;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EntregasApi.Services;

public interface IGoogleTtsService
{
    Task<string> SynthesizeAsync(string text);
}

public class GoogleTtsService : IGoogleTtsService
{
    private readonly TextToSpeechClient _client;
    private readonly IConfiguration _config;
    private readonly ILogger<GoogleTtsService> _logger;

    public GoogleTtsService(IConfiguration config, ILogger<GoogleTtsService> logger)
    {
        _config = config;
        _logger = logger;

        var builder = new TextToSpeechClientBuilder();

        // 1. Buscamos el JSON directamente en las variables de entorno de Render
        string credentialsJson = Environment.GetEnvironmentVariable("GOOGLE_CREDENTIALS_JSON");

        if (!string.IsNullOrWhiteSpace(credentialsJson))
        {
            // MODO PRODUCCIÓN (RENDER): Usamos el JSON directo de la memoria
            builder.JsonCredentials = credentialsJson;
        }
        else
        {
            // MODO DESARROLLO (LOCAL): Usamos la ruta de tu computadora
            builder.CredentialsPath = @"C:\Codigos\cami-voz.json";
        }

        _client = builder.Build();
    }

    public async Task<string> SynthesizeAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        // Limpiar markdown residual antes de sintetizar
        var cleanText = text
            .Replace("**", "").Replace("*", "").Replace("_", "")
            .Replace("#", "").Replace("`", "");

        var input = new SynthesisInput { Text = cleanText };

        var voiceName = _config["Cami:TtsVoice"] ?? "es-US-Chirp3-HD-Kore";
        var langCode = voiceName.Length >= 5 ? voiceName[..5] : "es-US";

        var voiceSelection = new VoiceSelectionParams
        {
            LanguageCode = langCode,
            Name = voiceName
        };

        var audioConfig = new AudioConfig
        {
            AudioEncoding = AudioEncoding.Mp3,
            Pitch = _config.GetValue<double>("Cami:TtsPitch", 0),
            SpeakingRate = _config.GetValue<double>("Cami:TtsSpeed", 1.0)
        };

        try
        {
            var response = await _client.SynthesizeSpeechAsync(input, voiceSelection, audioConfig);
            return response.AudioContent.ToBase64();
        }
        catch (Exception ex) when (voiceName.Contains("Chirp3"))
        {
            // Fallback a WaveNet si Chirp3-HD no está disponible en el plan
            _logger.LogWarning(ex, "Chirp3-HD no disponible, usando fallback WaveNet");
            voiceSelection.Name = "es-US-Wavenet-A";
            var response = await _client.SynthesizeSpeechAsync(input, voiceSelection, audioConfig);
            return response.AudioContent.ToBase64();
        }
    }
}
