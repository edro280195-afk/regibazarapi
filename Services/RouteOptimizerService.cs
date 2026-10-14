using System.Net.Http.Json;
using System.Text.Json;
using EntregasApi.Models;

namespace EntregasApi.Services;

public class RouteOptimizerService : IRouteOptimizerService
{
    private const string OpenRouteServiceEndpoint = "https://api.heigit.org/vroom/v0";
    private const int MaxProviderLocations = 50;

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RouteOptimizerService> _logger;

    public RouteOptimizerService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<RouteOptimizerService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public Task<OptimizedRoute> OptimizeAsync(
        List<RouteStop> stops,
        double startLat,
        double startLng,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (stops == null || stops.Count == 0)
            return Task.FromResult(new OptimizedRoute(new List<string>(), 0, 0, "empty"));

        var withCoords = stops.Where(s => s.Latitude.HasValue && s.Longitude.HasValue).ToList();
        var withoutCoords = stops.Where(s => !s.Latitude.HasValue || !s.Longitude.HasValue).ToList();

        if (withCoords.Count == 0)
            return Task.FromResult(new OptimizedRoute(stops.Select(s => s.Id).ToList(), 0, 0, "no-coords"));

        if (withCoords.Count == 1)
        {
            var single = withCoords.Concat(withoutCoords).Select(s => s.Id).ToList();
            return Task.FromResult(new OptimizedRoute(single, 0, 0, "single-stop"));
        }

        int n = withCoords.Count;

        // 1) Construir matriz local sin Google.
        var hav = BuildHaversineMatrix(withCoords, startLat, startLng);
        var durMatrix = hav.dur;
        var distMatrix = hav.dist;
        const string source = "haversine+2opt";

        // ── 2) Resolver TSP de ruta ABIERTA: inicio fijo en depot (nodo 0), final libre ──
        // Costo = tiempo de viaje (segundos). 2-opt elimina los cruces del nearest-neighbor greedy.
        var orderIdx = SolveOpenRoute(durMatrix, n); // índices de nodo 1..n en orden

        var orderedStops = orderIdx.Select(i => withCoords[i - 1]).ToList();

        // Totales a partir de la matriz (depot -> primera -> ... -> última, sin regreso).
        double totalDist = 0, totalDur = 0;
        int prev = 0;
        foreach (var idx in orderIdx)
        {
            totalDist += distMatrix[prev][idx];
            totalDur += durMatrix[prev][idx];
            prev = idx;
        }
        int distanceMeters = (int)Math.Round(totalDist);
        int durationSeconds = (int)Math.Round(totalDur);

        var allIds = orderedStops.Select(s => s.Id)
            .Concat(withoutCoords.Select(s => s.Id))
            .ToList();

        return Task.FromResult(new OptimizedRoute(allIds, distanceMeters, durationSeconds, source));
    }

    public async Task<OptimizedRoute> OptimizeWithRoadNetworkAsync(
        List<RouteStop> stops,
        double startLat,
        double startLng,
        CancellationToken ct = default)
    {
        var localResult = await OptimizeAsync(stops, startLat, startLng, ct);
        var withCoords = stops
            .Where(stop => stop.Latitude.HasValue && stop.Longitude.HasValue)
            .ToList();
        var withoutCoords = stops
            .Where(stop => !stop.Latitude.HasValue || !stop.Longitude.HasValue)
            .ToList();

        if (withCoords.Count < 2)
            return localResult;

        var apiKey = _configuration["OpenRouteService:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return CreateFallback(localResult,
                "OpenRouteService todavía no está configurado. Se usó el motor local.");
        }

        if (withCoords.Count + 1 > MaxProviderLocations)
        {
            return CreateFallback(localResult,
                $"ORS admite hasta {MaxProviderLocations - 1} entregas más la base por cálculo. Se usó el motor local.");
        }

        if (!IsValidCoordinate(startLat, startLng)
            || withCoords.Any(stop => !IsValidCoordinate(stop.Latitude!.Value, stop.Longitude!.Value))
            || stops.Select(stop => stop.Id).Distinct(StringComparer.Ordinal).Count() != stops.Count)
        {
            return CreateFallback(localResult,
                "Hay ubicaciones incompletas o inválidas. Se usó el motor local.");
        }

        var stopByJobId = withCoords
            .Select((stop, index) => new { JobId = index + 1, Stop = stop })
            .ToDictionary(item => item.JobId, item => item.Stop.Id);

        var payload = new
        {
            jobs = withCoords.Select((stop, index) => new
            {
                id = index + 1,
                location = new[] { stop.Longitude!.Value, stop.Latitude!.Value }
            }),
            vehicles = new[]
            {
                new
                {
                    id = 1,
                    profile = "driving-car",
                    start = new[] { startLng, startLat },
                    costs = new { per_hour = 3600, per_km = 0 }
                }
            },
            options = new { g = true }
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, OpenRouteServiceEndpoint);
            request.Headers.TryAddWithoutValidation("Authorization", apiKey);
            request.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "ORS respondió HTTP {StatusCode} para una ruta con {StopCount} entregas; se usará el motor local.",
                    (int)response.StatusCode,
                    withCoords.Count);
                return CreateFallback(localResult,
                    "OpenRouteService no respondió correctamente. Se usó el motor local.");
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: ct);
            var root = document.RootElement;

            if (root.TryGetProperty("code", out var responseCode)
                && responseCode.ValueKind == JsonValueKind.Number
                && responseCode.TryGetInt32(out var code)
                && code != 0)
            {
                return CreateFallback(localResult,
                    "OpenRouteService no pudo calcular este recorrido. Se usó el motor local.");
            }

            if (root.TryGetProperty("unassigned", out var unassigned)
                && unassigned.ValueKind == JsonValueKind.Array
                && unassigned.GetArrayLength() > 0)
            {
                return CreateFallback(localResult,
                    "OpenRouteService no pudo ubicar todas las entregas. Se usó el motor local.");
            }

            if (!root.TryGetProperty("routes", out var routes)
                || routes.ValueKind != JsonValueKind.Array
                || routes.GetArrayLength() != 1)
            {
                return CreateFallback(localResult,
                    "OpenRouteService devolvió una ruta incompleta. Se usó el motor local.");
            }

            var route = routes[0];
            if (!route.TryGetProperty("steps", out var steps)
                || steps.ValueKind != JsonValueKind.Array)
            {
                return CreateFallback(localResult,
                    "OpenRouteService no devolvió el orden de las entregas. Se usó el motor local.");
            }

            var orderedStops = new List<string>(withCoords.Count);
            var assignedJobIds = new HashSet<int>();
            foreach (var step in steps.EnumerateArray())
            {
                if (!step.TryGetProperty("type", out var type)
                    || type.ValueKind != JsonValueKind.String
                    || !string.Equals(type.GetString(), "job", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!step.TryGetProperty("id", out var idElement)
                    || !idElement.TryGetInt32(out var jobId)
                    || !stopByJobId.TryGetValue(jobId, out var stopId)
                    || !assignedJobIds.Add(jobId))
                {
                    return CreateFallback(localResult,
                        "OpenRouteService devolvió identificadores incompletos. Se usó el motor local.");
                }

                orderedStops.Add(stopId);
            }

            if (assignedJobIds.Count != withCoords.Count)
            {
                return CreateFallback(localResult,
                    "OpenRouteService no incluyó todas las entregas. Se usó el motor local.");
            }

            orderedStops.AddRange(withoutCoords.Select(stop => stop.Id));
            var distanceMeters = ReadNonNegativeInt(route, "distance");
            var durationSeconds = ReadNonNegativeInt(route, "duration");
            var geometry = route.TryGetProperty("geometry", out var geometryElement)
                && geometryElement.ValueKind == JsonValueKind.String
                    ? geometryElement.GetString()
                    : null;

            if (distanceMeters <= 0 || durationSeconds <= 0 || string.IsNullOrWhiteSpace(geometry))
            {
                return CreateFallback(localResult,
                    "OpenRouteService devolvió datos incompletos para dibujar la ruta. Se usó el motor local.");
            }

            return new OptimizedRoute(
                orderedStops,
                distanceMeters,
                durationSeconds,
                "ors-vroom",
                geometry);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException
            or HttpRequestException
            or JsonException
            or InvalidOperationException)
        {
            _logger.LogWarning(ex,
                "Falló ORS para una ruta con {StopCount} entregas; se usará el motor local.",
                withCoords.Count);
            return CreateFallback(localResult,
                "No se pudo conectar con OpenRouteService. Se usó el motor local.");
        }
    }

    private static OptimizedRoute CreateFallback(OptimizedRoute localResult, string message) =>
        localResult with { Source = "haversine-fallback", Message = message };

    private static bool IsValidCoordinate(double latitude, double longitude) =>
        double.IsFinite(latitude)
        && double.IsFinite(longitude)
        && latitude is >= -90 and <= 90
        && longitude is >= -180 and <= 180;

    private static int ReadNonNegativeInt(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }

        var parsed = value.TryGetInt32(out var integer)
            ? integer
            : (int)Math.Round(value.GetDouble());
        return Math.Max(0, parsed);
    }

    private static (double[][] dur, double[][] dist) BuildHaversineMatrix(
        List<RouteStop> stops, double depotLat, double depotLng)
    {
        int N = stops.Count + 1;
        var pts = new List<(double lat, double lng)>(N) { (depotLat, depotLng) };
        foreach (var s in stops) pts.Add((s.Latitude!.Value, s.Longitude!.Value));

        var dur = new double[N][];
        var dist = new double[N][];
        for (int i = 0; i < N; i++) { dur[i] = new double[N]; dist[i] = new double[N]; }

        for (int i = 0; i < N; i++)
            for (int j = 0; j < N; j++)
            {
                if (i == j) continue;
                double km = HaversineKm(pts[i].lat, pts[i].lng, pts[j].lat, pts[j].lng);
                dist[i][j] = km * 1000.0;
                dur[i][j] = km / 30.0 * 3600.0;
            }

        return (dur, dist);
    }

    // ───────────────────────────────────────────────────────────────────
    //  TSP de ruta abierta: nearest-neighbor (semilla) + mejora 2-opt.
    //  cost[0] = depot fijo al inicio; sin regreso (final libre).
    // ───────────────────────────────────────────────────────────────────
    private static List<int> SolveOpenRoute(double[][] cost, int n)
    {
        var path = new List<int>(n);
        var visited = new bool[n + 1];
        visited[0] = true;
        int current = 0;

        for (int step = 0; step < n; step++)
        {
            int best = -1;
            double bestC = double.MaxValue;
            for (int j = 1; j <= n; j++)
            {
                if (visited[j]) continue;
                if (cost[current][j] < bestC) { bestC = cost[current][j]; best = j; }
            }
            path.Add(best);
            visited[best] = true;
            current = best;
        }

        TwoOptOpen(path, cost);
        return path;
    }

    private static void TwoOptOpen(List<int> path, double[][] cost)
    {
        // full = [depot(0), path...]; ruta abierta -> sin arista de regreso al depot.
        var full = new List<int>(path.Count + 1) { 0 };
        full.AddRange(path);

        bool improved = true;
        int guard = 0;
        while (improved && guard++ < 80)
        {
            improved = false;
            for (int i = 1; i < full.Count - 1; i++)
            {
                for (int j = i + 1; j < full.Count; j++)
                {
                    int a = full[i - 1];
                    int b = full[i];
                    int c = full[j];
                    bool hasNext = j + 1 < full.Count;
                    int d = hasNext ? full[j + 1] : -1;

                    double before = cost[a][b] + (hasNext ? cost[c][d] : 0);
                    double after = cost[a][c] + (hasNext ? cost[b][d] : 0);

                    if (after + 1e-9 < before)
                    {
                        full.Reverse(i, j - i + 1);
                        improved = true;
                    }
                }
            }
        }

        path.Clear();
        for (int k = 1; k < full.Count; k++) path.Add(full[k]);
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
                if (dist < minDistance) { minDistance = dist; nearest = order; nearestIdx = i; }
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
