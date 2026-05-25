using EntregasApi.Models;

namespace EntregasApi.Services;

/// <summary>Una parada genérica que puede ser una Order o un TandaParticipant.</summary>
public record RouteStop(
    string Id,           // identificador único, puede ser "order:42" o "tanda:guid"
    double? Latitude,
    double? Longitude
);

public record OptimizedRoute(
    /// <summary>Los IDs de stops en el orden óptimo (incluye stops sin coords al final).</summary>
    List<string> OrderedStopIds,
    /// <summary>Distancia total en metros (0 si no se pudo calcular).</summary>
    int DistanceMeters,
    /// <summary>Duración total estimada en segundos (0 si no se pudo calcular).</summary>
    int DurationSeconds,
    /// <summary>Origen real usado por el optimizador (sirve para mostrar al usuario).</summary>
    string Source,
    /// <summary>Polyline encoded de Google (siguiendo calles reales). Null si fallback Haversine.</summary>
    string? PolylineEncoded = null
);

public interface IRouteOptimizerService
{
    /// <summary>
    /// Calcula el orden óptimo de paradas. Implementación principal usa Google Routes API v2
    /// con tráfico en tiempo real. Si falla cae a Haversine + Nearest Neighbor.
    /// </summary>
    Task<OptimizedRoute> OptimizeAsync(List<RouteStop> stops, double startLat, double startLng, CancellationToken ct = default);

    // Legacy sync API — usada por CamiService y por consumidores que aún no migran.
    List<Order> OptimizeRoute(List<Order> orders, double startLat, double startLng);
}
