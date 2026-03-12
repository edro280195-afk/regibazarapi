using Google.Cloud.TextToSpeech.V1;
using Microsoft.Extensions.Configuration;

namespace EntregasApi.Services;

public interface IGoogleTtsService
{
    Task<string> SynthesizeAsync(string text);
}

public class GoogleTtsService : IGoogleTtsService
{
    private readonly TextToSpeechClient _client;
    private readonly IConfiguration _config;

    public GoogleTtsService(IConfiguration config)
    {
        _config = config;
        
        // El SDK busca automáticamente la variable de entorno GOOGLE_APPLICATION_CREDENTIALS
        // Alternativamente, podemos construirlo con un API Key si el servicio lo soporta
        // Para Text-To-Speech usualmente se requiere Service Account, pero probaremos con la Key de Gemini primero.
        var apiKey = _config["Gemini:ApiKey"];
        _client = new TextToSpeechClientBuilder
        {
            ApiKey = apiKey
        }.Build();
    }

    public async Task<string> SynthesizeAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var input = new SynthesisInput { Text = text };

        // Configuración de la voz (Neural2/Journey/Coquette style)
        var voiceSelection = new VoiceSelectionParams
        {
            LanguageCode = "es-MX",
            // es-MX-Neural2-A es una voz femenina muy clara y profesional
            Name = "es-MX-Neural2-A"
        };

        var audioConfig = new AudioConfig
        {
            AudioEncoding = AudioEncoding.Mp3,
            Pitch = 0,
            SpeakingRate = 1.0
        };

        var response = await _client.SynthesizeSpeechAsync(input, voiceSelection, audioConfig);

        // Devolvemos el audio convertido a Base64 para que el frontend lo pueda reproducir fácilmente
        return response.AudioContent.ToBase64();
    }
}
