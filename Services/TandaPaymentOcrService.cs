using System.Globalization;
using System.Text.RegularExpressions;
using Tesseract;

namespace EntregasApi.Services;

public interface ITandaPaymentOcrService
{
    Task<TandaPaymentOcrResult> ExtractAsync(Stream image, CancellationToken cancellationToken = default);
}

public sealed record TandaPaymentOcrResult(
    decimal? Amount,
    DateTime? DepositDate,
    string Text,
    decimal? Confidence,
    string? Error = null);

public sealed class TandaPaymentOcrService : ITandaPaymentOcrService
{
    private readonly IConfiguration _configuration;

    public TandaPaymentOcrService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<TandaPaymentOcrResult> ExtractAsync(Stream image, CancellationToken cancellationToken = default)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"tanda-proof-{Guid.NewGuid():N}.png");
        try
        {
            await using (var output = File.Create(tempFile))
            {
                await image.CopyToAsync(output, cancellationToken);
            }

            var dataPath = _configuration["LiveCapture:TesseractDataPath"]
                ?? "/usr/share/tesseract-ocr/5/tessdata";
            var language = _configuration["LiveCapture:TesseractLanguage"] ?? "spa+eng";

            if (!Directory.Exists(dataPath))
            {
                return new TandaPaymentOcrResult(null, null, string.Empty, null,
                    $"No existe el directorio de datos OCR: {dataPath}");
            }

            using var engine = new TesseractEngine(dataPath, language, EngineMode.Default);
            using var pix = Pix.LoadFromFile(tempFile);
            using var page = engine.Process(pix);
            var text = page.GetText() ?? string.Empty;
            var confidence = (decimal)Math.Round(page.GetMeanConfidence(), 4);

            return new TandaPaymentOcrResult(
                ExtractAmount(text),
                ExtractDepositDate(text),
                text.Trim(),
                confidence);
        }
        catch (Exception ex) when (ex is TesseractException or DllNotFoundException or FileNotFoundException)
        {
            return new TandaPaymentOcrResult(null, null, string.Empty, null, ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
            catch
            {
                // El archivo temporal no debe impedir guardar el comprobante.
            }
        }
    }

    public static decimal? ExtractAmount(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var patterns = new[]
        {
            @"(?i)(?:total|importe|monto|dep[oó]sito|pago|transferencia|amount)[^\d]{0,24}(?:mxn|mx\$|\$)?\s*([0-9]{1,3}(?:,[0-9]{3})*(?:\.[0-9]{1,2})?|[0-9]+(?:\.[0-9]{1,2})?)",
            @"(?:\$|mxn|mx\$)\s*([0-9]{1,3}(?:,[0-9]{3})*(?:\.[0-9]{1,2})?|[0-9]+(?:\.[0-9]{1,2})?)"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) continue;

            var normalized = match.Groups[1].Value.Replace(",", string.Empty);
            if (decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
                && amount >= 0)
            {
                return amount;
            }
        }

        return null;
    }

    public static DateTime? ExtractDepositDate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var match = Regex.Match(
            text,
            @"(?<date>\b(?:\d{1,2}[/-]\d{1,2}[/-]\d{2,4}|\d{4}[/-]\d{1,2}[/-]\d{1,2})\b)(?:[ T,]+(?<time>\d{1,2}:\d{2}(?::\d{2})?\s*(?:a\.?m\.?|p\.?m\.?)?))?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!match.Success) return null;

        var raw = $"{match.Groups["date"].Value} {match.Groups["time"].Value}".Trim();
        if (!DateTime.TryParse(raw, new CultureInfo("es-MX"), DateTimeStyles.AllowWhiteSpaces, out var localDate))
            return null;

        localDate = DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified);
        var timeZone = FindBusinessTimeZone();
        return TimeZoneInfo.ConvertTimeToUtc(localDate, timeZone);
    }

    private static TimeZoneInfo FindBusinessTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Matamoros"); }
        catch
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time (Mexico)"); }
            catch { return TimeZoneInfo.Utc; }
        }
    }
}
