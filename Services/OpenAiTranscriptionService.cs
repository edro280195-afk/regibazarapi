using System.Net.Http.Headers;
using System.Text.Json;
using EntregasApi.Models;
using Microsoft.Extensions.Options;

namespace EntregasApi.Services;

public interface IOpenAiTranscriptionService
{
    Task<List<LiveTranscriptSegment>> TranscribeChunksAsync(
        int liveSessionId,
        IReadOnlyList<string> audioChunkPaths,
        int chunkSeconds,
        CancellationToken cancellationToken);
}

public class OpenAiTranscriptionService : IOpenAiTranscriptionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly OpenAiOptions _options;
    private readonly ILogger<OpenAiTranscriptionService> _logger;

    public OpenAiTranscriptionService(
        HttpClient httpClient,
        IOptions<OpenAiOptions> options,
        ILogger<OpenAiTranscriptionService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<List<LiveTranscriptSegment>> TranscribeChunksAsync(
        int liveSessionId,
        IReadOnlyList<string> audioChunkPaths,
        int chunkSeconds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("Falta configurar OpenAI:ApiKey para transcribir lives.");

        var allSegments = new List<LiveTranscriptSegment>();

        for (var i = 0; i < audioChunkPaths.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = audioChunkPaths[i];
            if (!File.Exists(path)) continue;

            var offsetSeconds = i * chunkSeconds;
            _logger.LogInformation("Transcribiendo live {LiveSessionId}, chunk {Chunk}/{Total}", liveSessionId, i + 1, audioChunkPaths.Count);

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/transcriptions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

            await using var fileStream = File.OpenRead(path);
            using var form = new MultipartFormDataContent();
            var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(fileContent, "file", Path.GetFileName(path));
            form.Add(new StringContent(_options.TranscriptionModel), "model");
            form.Add(new StringContent("es"), "language");
            form.Add(new StringContent("verbose_json"), "response_format");
            form.Add(new StringContent("segment"), "timestamp_granularities[]");

            request.Content = form;
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"OpenAI rechazo la transcripcion: {(int)response.StatusCode} {body}");
            }

            var transcription = JsonSerializer.Deserialize<VerboseTranscriptionResponse>(body, JsonOptions);
            if (transcription?.Segments is { Count: > 0 })
            {
                allSegments.AddRange(transcription.Segments
                    .Where(s => !string.IsNullOrWhiteSpace(s.Text))
                    .Select(s => new LiveTranscriptSegment
                    {
                        LiveSessionId = liveSessionId,
                        StartSeconds = offsetSeconds + s.Start,
                        EndSeconds = offsetSeconds + s.End,
                        Text = s.Text.Trim()
                    }));
            }
            else if (!string.IsNullOrWhiteSpace(transcription?.Text))
            {
                allSegments.Add(new LiveTranscriptSegment
                {
                    LiveSessionId = liveSessionId,
                    StartSeconds = offsetSeconds,
                    EndSeconds = offsetSeconds + chunkSeconds,
                    Text = transcription.Text.Trim()
                });
            }
        }

        return allSegments;
    }

    private sealed class VerboseTranscriptionResponse
    {
        public string? Text { get; set; }
        public List<VerboseTranscriptionSegment> Segments { get; set; } = new();
    }

    private sealed class VerboseTranscriptionSegment
    {
        public double Start { get; set; }
        public double End { get; set; }
        public string Text { get; set; } = string.Empty;
    }
}
