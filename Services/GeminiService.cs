using System.Text.Json;
using Google.GenAI;
using Google.GenAI.Types;
using EntregasApi.DTOs;

namespace EntregasApi.Services;

public record AiParsedOrder(string ClientName, string ProductName, int Quantity, decimal UnitPrice);

public interface IGeminiService
{
    Task<List<AiParsedOrder>> ParseLiveTextAsync(string text, List<AiParsedOrder>? currentState = null);
    Task<List<AiInsightDto>> AnalyzeReportAsync(JsonElement report);
    Task<AiRouteSelectionResponse> SelectOrdersForRouteAsync(AiRouteSelectionRequest request);
}

public class GeminiService : IGeminiService
{
    private readonly Google.GenAI.Client _client;
    private readonly ILogger<GeminiService> _logger;

    public GeminiService(IConfiguration config, ILogger<GeminiService> logger)
    {
        _logger = logger;
        var apiKey = config["Gemini:ApiKey"];

        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("¡Falta Gemini:ApiKey en appsettings.json!");
        }

        // El nuevo cliente oficial de Google GenAI
        _client = new Google.GenAI.Client(apiKey: apiKey);
    }

    public async Task<List<AiParsedOrder>> ParseLiveTextAsync(string text, List<AiParsedOrder>? currentState = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return currentState ?? new List<AiParsedOrder>();

        try
        {
            _logger.LogInformation("Llamando al cerebro de Gemini...");

            // Juntamos las reglas con el dictado de Miel
            var systemPrompt = @"Eres el motor de IA de Regi Bazar trabajando en 'Modo Contextual'. 
Tu trabajo es gestionar un carrito de compras en tiempo real. 
Recibirás el ESTADO ACTUAL del carrito en JSON, y una NUEVA INSTRUCCIÓN dictada por voz.
Debes aplicar la instrucción al estado actual y devolver el ESTADO ACTUALIZADO completo como un arreglo JSON.

Reglas Vitales:
1. Devuelve ÚNICAMENTE el arreglo JSON actualizado entero: [{ ""clientName"": string, ""productName"": string, ""quantity"": number, ""unitPrice"": number }]
2. NUNCA devuelvas Markdown (como ```json) ni explicaciones de texto, solo el JSON puro y crudo.
3. Si la instrucción pide algo nuevo, agrégalo al estado.
4. Si la instrucción pide 'otra' o 'más', busca qué estaba comprando esa persona y súmale la cantidad.
5. Si cancela o 'quita' algo, búscalo y réstale la cantidad, o elimínalo si llega a 0.
6. Si un producto no tiene precio dictado, pon 0, a menos que el mismo producto ya tenga precio en el carrito, en ese caso usa ese precio.
7. Si el dictado incluye a una persona que ya está en el carrito, junta sus productos si tienen exactamente el mismo nombre y precio sumando su cantidad. Si es producto diferente, haz un nuevo bloque {} para esa misma persona.";

            var currentJson = currentState == null || currentState.Count == 0 ? "[]" : JsonSerializer.Serialize(currentState);
            var finalPrompt = $"{systemPrompt}\n\nESTADO ACTUAL DEL CARRITO:\n{currentJson}\n\nNUEVA INSTRUCCIÓN DEL USUARIO:\n\"{text}\"";

            // ¡La magia del modo JSON estructurado!
            var config = new GenerateContentConfig
            {
                Temperature = 0.1f, // Cero creatividad, pura precisión matemática
                ResponseMimeType = "application/json" // Obliga al modelo a no salirse del formato
            };

            // Llamada al modelo Flash (el rey de la velocidad)
            var response = await _client.Models.GenerateContentAsync(
                model: "gemini-2.5-flash",
                contents: finalPrompt,
                config: config
            );

            // Extraemos la respuesta de manera segura
            var jsonText = response.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "[]";

            // Convertimos la respuesta cruda a tus objetos de C#
            var orders = JsonSerializer.Deserialize<List<AiParsedOrder>>(jsonText, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return orders ?? new List<AiParsedOrder>();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Gemini no devolvió un JSON válido. Revisa el dictado.");
            return new List<AiParsedOrder>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Se cayó la conexión con Google API.");
            throw; // Mantenemos la alerta para el Frontend
        }
    }

    public async Task<List<AiInsightDto>> AnalyzeReportAsync(JsonElement report)
    {
        try
        {
            var systemInstruction = @"Eres el 'Cerebro' (Business Intelligence) de 'Regi Bazar'. 
Tu objetivo es analizar la data rigurosamente y generar entre 3 y 5 'Insights' estratégicos.

Reglas Vitales de formato:
1. Devuelve ÚNICAMENTE un arreglo JSON válido con esta estructura:
[{ ""category"": ""string"", ""title"": ""string"", ""description"": ""string"", ""actionableAdvice"": ""string"", ""icon"": ""string"" }]
2. NUNCA devuelvas Markdown ni explicaciones, solo el JSON puro.
3. El campo 'category' DEBE SER: 'Finanzas', 'Ventas', 'Clientas', 'Riesgo', o 'Operación'.
4. El campo 'icon' debe ser un SOLO EMOJI representativo (ej. 💸, 🚨, 👑, 🚚, 📈).
5. Usa un tono analítico pero empático ('coquette' / bazar friendly).
6. Si ves fugas (TotalRevenue > TotalCollected o PendingAmount grande), adviértelo como 'Riesgo' con icono 🚨.
7. Observa la tasa de éxito (DeliveredOrders vs TotalOrders).";

            var config = new GenerateContentConfig
            {
                SystemInstruction = new Content { Role = "system", Parts = new List<Part> { new Part { Text = systemInstruction } } },
                ResponseMimeType = "application/json",
                Temperature = 0.4f
                // 🔥 ELIMINAMOS MaxOutputTokens para dejar que el modelo respire libremente
            };

            var reportJson = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            var finalPrompt = $"DATOS DEL REPORTE DEL NEGOCIO:\n{reportJson}";

            var response = await _client.Models.GenerateContentAsync("gemini-2.5-flash", finalPrompt, config);

            // 🔥 EL DIAGNÓSTICO DEL ARQUITECTO 🔥
            // Esto nos dirá el motivo exacto por el cual Google detuvo la escritura
            var finishReason = response.Candidates?[0]?.FinishReason;
            Console.WriteLine($"\n[Gemini Finish Reason]: {finishReason}\n");

            var resultText = response?.Text ?? "[]";

            var cleanJson = resultText
                .Replace("```json", "")
                .Replace("```", "")
                .Trim();

            try
            {
                var insights = JsonSerializer.Deserialize<List<AiInsightDto>>(cleanJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                return insights ?? new List<AiInsightDto>();
            }
            catch (JsonException jsonEx)
            {
                Console.WriteLine($"[Raw Gemini Text]:\n{cleanJson}");
                Console.WriteLine($"[Gemini JSON Parse Error]: {jsonEx.Message}");
                throw new Exception($"Gemini devolvió texto incompleto. Motivo de corte: {finishReason}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Gemini API Error] Error general analizando reporte: {ex.Message}");
            throw;
        }
    }

    public async Task<AiRouteSelectionResponse> SelectOrdersForRouteAsync(AiRouteSelectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.VoiceCommand) || request.AvailableOrders == null || request.AvailableOrders.Count == 0)
        {
            return new AiRouteSelectionResponse(new List<int>(), "No hay órdenes disponibles o no se dictó instrucción.");
        }

        try
        {
            _logger.LogInformation("🧠 Calculando ruta por voz: {Command}", request.VoiceCommand);

            var systemInstruction = @"Eres el Asistente Logístico de Inteligencia Artificial para Regi Bazar.
Tu tarea es escuchar una instrucción de voz dictada por el administrador y seleccionar exactamente qué pedidos (Órdenes) deben asignarse a una nueva ruta de reparto.

REGLAS ESTRICTAS:
1. Recibirás la instrucción de voz del administrador (texto tal y como lo reconoció el micrófono, puede tener errores ortográficos).
2. Recibirás un JSON con la lista de 'Órdenes Disponibles' (pendientes de entregar). Cada orden tiene un 'id', 'clientName', 'status', 'clientAddress', etc.
3. Debes cruzar la instrucción de voz con la lista de órdenes disponibles.
4. Identifica las órdenes correctas basadas en el nombre del cliente. Toma en cuenta lo siguiente:
   - APODOS o NOMBRES INCOMPLETOS: Si dice 'Mary' y existe 'Mary Carmen' o 'Maria Ramirez', selecciónalo.
   - ERRORES FONÉTICOS (Fuzzy Matching): El micrófono suele equivocarse. Si dice 'Meleny', podría ser 'Melanie'. Si dice 'Gricy', podría ser 'Grisi' o 'Grisy'. Usa tu mejor juicio deductivo.
   - Si detectas una similitud fonética obvia con algún cliente en la lista, asume que es ese.
5. Tu respuesta DEBE ser EXCLUSIVAMENTE un objeto JSON válido con este formato exacto:
{
  ""selectedOrderIds"": [12, 15, 30],
  ""aiConfirmationMessage"": ""Breve mensaje de éxito, ej: '¡Claro! Armé la ruta con los pedidos de Susana, Mary y los 3 pendientes confirmados.'""
}
6. NADA de Markdown (` ```json `), NADA de texto fuera del JSON puro.
7. Si el administrador da una instrucción vaga como 'Agrégamelos todos', incluye todos los IDs de la lista.
8. Si no logras hacer coincidir ningún pedido, devuelve la lista vacía [] y un mensaje amigable pidiendo que repita.";

            var config = new GenerateContentConfig
            {
                SystemInstruction = new Content { Role = "system", Parts = new List<Part> { new Part { Text = systemInstruction } } },
                ResponseMimeType = "application/json",
                Temperature = 0.2f // Baja temperatura para precisión en la coincidencia
            };

            // Creamos un subconjunto ligero de datos para no saturar los tokens de Gemini
            var lightweightOrders = request.AvailableOrders.Select(o => new
            {
                id = o.Id,
                clientName = o.ClientName,
                address = o.ClientAddress,
                status = o.Status
            }).ToList();

            var ordersJson = JsonSerializer.Serialize(lightweightOrders, new JsonSerializerOptions { WriteIndented = true });
            
            var finalPrompt = $"INSTRUCCIÓN POR VOZ:\n\"{request.VoiceCommand}\"\n\nÓRDENES DISPONIBLES:\n{ordersJson}";

            var response = await _client.Models.GenerateContentAsync("gemini-1.5-pro", finalPrompt, config);

            var resultText = response?.Text ?? "{}";
            var cleanJson = resultText.Replace("```json", "").Replace("```", "").Trim();

            Console.WriteLine($"[Gemini Route Result]:\n{cleanJson}");

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = JsonSerializer.Deserialize<AiRouteSelectionResponse>(cleanJson, options);

            return result ?? new AiRouteSelectionResponse(new List<int>(), "Lo siento, la mente de Gemini divagó un momento.");
        }
        catch (JsonException ex)
        {
            _logger.LogError("Error de parseo JSON desde Gemini Routes: {Error}", ex.Message);
            return new AiRouteSelectionResponse(new List<int>(), "Hubo una pequeña confusión traduciendo los nombres.");
        }
        catch (Exception ex)
        {
            _logger.LogError("Error general en Gemini Routes: {Error}", ex.Message);
            throw;
        }
    }
}