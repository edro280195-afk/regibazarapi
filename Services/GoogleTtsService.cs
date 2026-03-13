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

        var input = new SynthesisInput { Text = text };
        // --- TRUCO PARA IMPRIMIR EL CATÁLOGO REAL ---
        //var listResponse = await _client.ListVoicesAsync(new ListVoicesRequest { LanguageCode = "es-MX" });
        //foreach (var v in listResponse.Voices)
        //{
        //    Console.WriteLine($"Voz disponible: {v.Name} - Tecnología: {v.Name.Split('-')[2]} - Género: {v.SsmlGender}");
        //}
        // --------------------------------------------

        // Configuración de la voz (Neural2/Journey/Coquette style)
        var voiceSelection = new VoiceSelectionParams
        {
            LanguageCode = "es-US",
            Name = "es-US-Wavenet-A"
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