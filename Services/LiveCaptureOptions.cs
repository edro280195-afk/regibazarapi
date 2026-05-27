namespace EntregasApi.Services;

public class LiveCaptureOptions
{
    public string YtDlpPath { get; set; } = "yt-dlp";
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public string TempDirectory { get; set; } = "/tmp/regibazar-live";
    public int TranscriptWindowSeconds { get; set; } = 300;
    public int TranscriptOverlapSeconds { get; set; } = 30;
    public int AudioChunkSeconds { get; set; } = 600;
    public int FrameEverySeconds { get; set; } = 3;
    public string? FrameCropFilter { get; set; }
    public int CrossMatchWindowSeconds { get; set; } = 180;
    public double KeywordMatchThreshold { get; set; } = 0.62;
    public string TesseractDataPath { get; set; } = "/usr/share/tesseract-ocr/5/tessdata";
    public string TesseractLanguage { get; set; } = "spa+eng";
}

public class OpenAiOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string TranscriptionModel { get; set; } = "whisper-1";
}

public class CloudflareR2Options
{
    public string AccountId { get; set; } = string.Empty;
    public string AccessKeyId { get; set; } = string.Empty;
    public string SecretAccessKey { get; set; } = string.Empty;
    public string BucketName { get; set; } = string.Empty;
    public string? ServiceUrl { get; set; }
    public string VideoPrefix { get; set; } = "live-videos";
}
