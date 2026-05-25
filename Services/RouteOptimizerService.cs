using System.Net.Http.Json;
using System.Text.Json;
using EntregasApi.Models;

namespace EntregasApi.Services;

public class RouteOptimizerService : IRouteOptimizerService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<RouteOptimizerService> _logger;

    public RouteOptimizerService(IHttpClientFactory httpFactory, IConfiguration config, ILogger<RouteOptimizerService> logger)
    {
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    public async Task<OptimizedRoute> OptimizeAsync(List<RouteStop> stops, double startLat, double startLng, CancellationToken ct = default)
    {
        if (stops == null || stops.Count == 0)
            return new OptimizedRoute(new List<string>(), 0, 0, "empty");

        var withCoords = stops.Where(s => s.Latitude.HasValue && s.Longitude.HasValue).ToList();
        var withoutCoords = stops.Where(s => !s.Latitude.HasValue || !s.Longitude.HasValue).ToList();

        // Si todo está sin coords no hay nada que optimizar, regresamos en el mismo orden.
        if (withCoords.Count == 0)
            return new OptimizedRoute(stops.Select(s => s.Id).ToList(), 0, 0, "no-coords");

        // 1 sola parada con coords: no necesita llamar a Google.
        if (withCoords.Count == 1)
        {
            var ordered = withCoords.Concat(withoutCoords).Select(s => s.Id).ToList();
            return new OptimizedRoute(ordered, 0, 0, "single-stop");
        }

        var apiKey = _config["Google:RoutesApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey) && apiKey != "dummy")
        {
            try
            {
                var result = await OptimizeWithGoogleRoutesAsync(withCoords, startLat, startLng, apiKey, ct);
                if (result != null)
                {
                    // Anexamos las paradas sin coords al final (no hay manera de optimizarlas).
                    var allIds = result.OrderedStopIds.Concat(withoutCoords.Select(s => s.Id)).ToList();
                    return result with { OrderedStopIds = allIds };
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Google Routes API falló, usando fallback Haversine");
            }
        }

        // Fallback: Nearest Neighbor con Haversine.
        var fallback = NearestNeighborHaversine(withCoords, startLat, startLng);
        var fallbackIds = fallback.Concat(withoutCoords.Select(s => s.Id)).ToList();
        return new OptimizedRoute(fallbackIds, 0, 0, "haversine-fallback");
    }

    private async Task<OptimizedRoute?> OptimizeWithGoogleRoutesAsync(
        List<RouteStop> stops, double startLat, double startLng, string apiKey, CancellationToken ct)
    {
        // Routes API v2 acepta hasta 25 intermediate waypoints. Si hay más usamos fallback.
        if (stops.Count > 25)
        {
            _logger.LogInformation("Routes API v2 limita a 25 waypoints; tenemos {Count}, usando Haversine", stops.Count);
            return null;
        }

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(15);

        var body = new
        {
            origin = new { location = new { latLng = new { latitude = startLat, longitude = startLng } } },
            // Destination = depot mismo (round trip). El driver vuelve al negocio al final.
            destination = new { location = new { latLng = new { latitude = startLat, longitude = startLng } } },
            intermediates = stops.Select(s => new
            {
                location = new { latLng = new { latitude = s.Latitude!.Value, longitude = s.Longitude!.Value } }
            }).ToArray(),
            travelMode = "DRIVE",
            routingPreference = "TRAFFIC_AWARE",
            optimizeWaypointOrder = true,
            languageCode = "es-MX",
            units = "METRIC"
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://routes.googleapis.com/directions/v2:computeRoutes");
        req.Headers.Add("X-Goog-Api-Key", apiKey);
        req.Headers.Add("X-Goog-FieldMask",
            "routes.distanceMeters,routes.duration,routes.optimizedIntermediateWaypointIndex,routes.polyline.encodedPolyline");
        req.Content = JsonContent.Create(body);

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Routes API devolvió {Status}: {Body}", resp.StatusCode, errBody);
            return null;
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("routes", out var routes) || routes.GetArrayLength() == 0)
            return null;

        var route = routes[0];
        int distanceMeters = route.TryGetProperty("distanceMeters", out var dm) ? dm.GetInt32() : 0;
        int durationSeconds = 0;
        if (route.TryGetProperty("duration", out var dur))
        {
            // Formato: "1234s"
            var durStr = dur.GetString() ?? "0s";
            int.TryParse(durStr.TrimEnd('s'), out durationSeconds);
        }

        List<int> order;
        if (route.TryGetProperty("optimizedIntermediateWaypointIndex", out var idxArr))
        {
            order = idxArr.EnumerateArray().Select(e => e.GetInt32()).ToList();
        }
        else
        {
            // No retornó orden optimizado, usamos el orden original.
            order = Enumerable.Range(0, stops.Count).ToList();
        }

        string? polyline = null;
        if (route.TryGetProperty("polyline", out var poly) && poly.TryGetProperty("encodedPolyline", out var ep))
            polyline = ep.GetString();

        var orderedIds = order.Select(i => stops[i].Id).ToList();
        return new OptimizedRoute(orderedIds, distanceMeters, durationSeconds, "google-routes-v2", polyline);
    }

    private List<string> NearestNeighborHaversine(List<RouteStop> stops, double startLat, double startLng)
    {
        var ordered = new List<string>();
        var remaining = new List<RouteStop>(stops);
        double currentLat = startLat;
        double currentLng = startLng;

        while (remaining.Count > 0)
        {
            var nearest = remaining
                .Select(s => new { s, dist = HaversineKm(currentLat, currentLng, s.Latitude!.Value, s.Longitude!.Value) })
                .OrderBy(x => x.dist)
                .First();
            ordered.Add(nearest.s.Id);
            currentLat = nearest.s.Latitude!.Value;
            currentLng = nearest.s.Longitude!.Value;
            remaining.Remove(nearest.s);
        }
        return ordered;
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371;
        double dLat = Math.PI * (lat2 - lat1) / 180.0;
        double dLon = Math.PI * (lon2 - lon1) / 180.0;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(Math.PI * lat1 / 180.0) * Math.Cos(Math.PI * lat2 / 180.0) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    // ── Legacy sync API ──
    public List<Order> OptimizeRoute(List<Order> orders, double startLat, double startLng)
    {
        if (orders == null || !orders.Any()) return new List<Order>();

        var withCoords = orders.Where(o => o.Client?.Latitude != null && o.Client?.Longitude != null).ToList();
        var withoutCoords = orders.Where(o => o.Client?.Latitude == null || o.Client?.Longitude == null).ToList();

        var optimized = new List<Order>();
        var remaining = new List<Order>(withCoords);
        double currentLat = startLat;
        double currentLng = startLng;

        while (remaining.Any())
        {
            Order? nearest = null;
            double minDistance = double.MaxValue;
            int nearestIdx = -1;
            for (int i = 0; i < remaining.Count; i++)
            {
                var order = remaining[i];
                double dist = HaversineKm(currentLat, currentLng, order.Client.Latitude!.Value, order.Client.Longitude!.Value);
                if (dist < minDistance)
                {
                    minDistance = dist;
                    nearest = order;
                    nearestIdx = i;
                }
            }

            if (nearest != null)
            {
                optimized.Add(nearest);
                currentLat = nearest.Client.Latitude!.Value;
                currentLng = nearest.Client.Longitude!.Value;
                remaining.RemoveAt(nearestIdx);
            }
        }

        optimized.AddRange(withoutCoords);
        return optimized;
    }
}
