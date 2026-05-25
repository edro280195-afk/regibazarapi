namespace EntregasApi.Services;

public record GeocodeResult(
    bool Success,
    double? Latitude,
    double? Longitude,
    string? FormattedAddress,
    string? Status,
    string? Error
);

public interface IGeocodingService
{
    /// <summary>Geocodea una dirección con Google Geocoding API.</summary>
    Task<GeocodeResult> GeocodeAsync(string address, CancellationToken ct = default);
}
