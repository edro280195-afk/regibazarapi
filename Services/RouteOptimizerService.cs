using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using EntregasApi.Models;

namespace EntregasApi.Services;

public class RouteOptimizerService : IRouteOptimizerService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<RouteOptimizerService> _logger;

    // Routes API v2 acepta hasta 25 waypoints intermedios por solicitud de computeRoutes.
    private const int MaxIntermediatesForPolyline = 25;
    // computeRouteMatrix permite hasta 625 elementos (orígenes × destinos) por request.
    private const int MatrixElementBudget = 600;

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

        if (withCoords.Count == 0)
            return new OptimizedRoute(stops.Select(s => s.Id).ToList(), 0, 0, "no-coords");

        if (withCoords.Count == 1)
        {
            var single = withCoords.Concat(withoutCoords).Select(s => s.Id).ToList();
            return new OptimizedRoute(single, 0, 0, "single-stop");
        }

        var apiKey = _config["Google:RoutesApiKey"];
        bool hasKey = !string.IsNullOrWhiteSpace(apiKey) && apiKey != "dummy";

        int n = withCoords.Count;

        // ── 1) Construir matriz de distancias/tiempos reales (carretera) ──
        double[][]? durMatrix = null;
        double[][]? distMatrix = null;
        string source;

        if (hasKey)
        {
            try
            {
                var built = await BuildDistanceMatrixAsync(withCoords, startLat, startLng, apiKey!, ct);
                if (built != null)
                {
                    durMatrix = built.Value.dur;
                    distMatrix = built.Value.dist;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "computeRouteMatrix falló, usando matriz Haversine");
            }
        }

        if (durMatrix != null && distMatrix != null)
        {
            source = "distance-matrix+2opt";
        }
        else
        {
            var hav = BuildHaversineMatrix(withCoords, startLat, startLng);
            durMatrix = hav.dur;
            distMatrix = hav.dist;
            source = "haversine+2opt";
        }

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

        // ── 3) Polyline para dibujar la ruta real (orden ya fijado, ruta abierta) ──
        string? polyline = null;
        if (hasKey)
        {
            try
            {
                var poly = await GetPolylineForOrderAsync(orderedStops, startLat, startLng, apiKey!, ct);
                if (poly != null)
                {
                    polyline = poly.Value.Polyline;
                    if (poly.Value.DistanceMeters > 0) distanceMeters = poly.Value.DistanceMeters;
                    if (poly.Value.DurationSeconds > 0) durationSeconds = poly.Value.DurationSeconds;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "computeRoutes (polyline) falló; el frontend dibujará con DirectionsService");
            }
        }

        var allIds = orderedStops.Select(s => s.Id)
            .Concat(withoutCoords.Select(s => s.Id))
            .ToList();

        return new OptimizedRoute(allIds, distanceMeters, durationSeconds, source, polyline);
    }

    // ───────────────────────────────────────────────────────────────────
    //  Matriz de distancias reales vía Routes API v2 (computeRouteMatrix)
    //  Nodo 0 = depot; nodos 1..n = paradas. Devuelve matrices (n+1)×(n+1).
    // ───────────────────────────────────────────────────────────────────
    private async Task<(double[][] dur, double[][] dist)?> BuildDistanceMatrixAsync(
        List<RouteStop> stops, double depotLat, double depotLng, string apiKey, CancellationToken ct)
    {
        int N = stops.Count + 1;
        var pts = new List<(double lat, double lng)>(N) { (depotLat, depotLng) };
        foreach (var s in stops) pts.Add((s.Latitude!.Value, s.Longitude!.Value));

        var dur = new double[N][];
        var dist = new double[N][];
        for (int i = 0; i < N; i++)
        {
            dur[i] = new double[N];
            dist[i] = new double[N];
            for (int j = 0; j < N; j++) { dur[i][j] = double.NaN; dist[i][j] = double.NaN; }
        }

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(20);

        var destPayload = pts.Select(p => new
        {
            waypoint = new { location = new { latLng = new { latitude = p.lat, longitude = p.lng } } }
        }).ToArray();

        int originsPerBlock = Math.Max(1, MatrixElementBudget / N);

        for (int start = 0; start < N; start += originsPerBlock)
        {
            int count = Math.Min(originsPerBlock, N - start);
            var originPayload = pts.Skip(start).Take(count).Select(p => new
            {
                waypoint = new { location = new { latLng = new { latitude = p.lat, longitude = p.lng } } }
            }).ToArray();

            var body = new
            {
                origins = originPayload,
                destinations = destPayload,
                travelMode = "DRIVE",
                routingPreference = "TRAFFIC_AWARE"
            };

            using var req = new HttpRequestMessage(HttpMethod.Post,
                "https://routes.googleapis.com/distanceMatrix/v2:computeRouteMatrix");
            req.Headers.Add("X-Goog-Api-Key", apiKey);
            req.Headers.Add("X-Goog-FieldMask", "originIndex,destinationIndex,duration,distanceMeters,condition");
            AddReferrer(req);
            req.Content = JsonContent.Create(body);

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("computeRouteMatrix devolvió {Status}: {Body}", resp.StatusCode, errBody);
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                int oi = el.TryGetProperty("originIndex", out var oiEl) ? oiEl.GetInt32() : 0;
                int di = el.TryGetProperty("destinationIndex", out var diEl) ? diEl.GetInt32() : 0;
                int globalOrigin = start + oi;

                var condition = el.TryGetProperty("condition", out var cEl) ? cEl.GetString() : null;
                if (condition == "ROUTE_NOT_FOUND") continue; // se rellena con Haversine abajo

                if (el.TryGetProperty("distanceMeters", out var dmEl) && dmEl.TryGetInt32(out var meters))
                    dist[globalOrigin][di] = meters;

                if (el.TryGetProperty("duration", out var durEl))
                {
                    var ds = (durEl.GetString() ?? "0s").TrimEnd('s');
                    if (double.TryParse(ds, NumberStyles.Any, CultureInfo.InvariantCulture, out var seconds))
                        dur[globalOrigin][di] = seconds;
                }
            }
        }

        // Rellenar celdas faltantes (route-not-found / errores parciales) con estimación Haversine.
        for (int i = 0; i < N; i++)
            for (int j = 0; j < N; j++)
            {
                if (i == j) { dur[i][j] = 0; dist[i][j] = 0; continue; }
                if (double.IsNaN(dist[i][j]) || double.IsNaN(dur[i][j]))
                {
                    double km = HaversineKm(pts[i].lat, pts[i].lng, pts[j].lat, pts[j].lng);
                    if (double.IsNaN(dist[i][j])) dist[i][j] = km * 1000.0;
                    if (double.IsNaN(dur[i][j])) dur[i][j] = km / 30.0 * 3600.0; // ~30 km/h urbano
                }
            }

        return (dur, dist);
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

    // ───────────────────────────────────────────────────────────────────
    //  Polyline de la ruta ya ordenada (ruta abierta: destino = última parada).
    //  NO usa optimizeWaypointOrder: el orden ya lo decidimos nosotros.
    // ───────────────────────────────────────────────────────────────────
    private async Task<(string? Polyline, int DistanceMeters, int DurationSeconds)?> GetPolylineForOrderAsync(
        List<RouteStop> ordered, double depotLat, double depotLng, string apiKey, CancellationToken ct)
    {
        if (ordered.Count == 0) return null;

        // computeRoutes admite máx. 25 intermedios. Con más paradas, el frontend dibuja con DirectionsService.
        if (ordered.Count - 1 > MaxIntermediatesForPolyline) return null;

        var last = ordered[^1];
        var intermediates = ordered.Take(ordered.Count - 1).Select(s => new
        {
            location = new { latLng = new { latitude = s.Latitude!.Value, longitude = s.Longitude!.Value } }
        }).ToArray();

        var body = new
        {
            origin = new { location = new { latLng = new { latitude = depotLat, longitude = depotLng } } },
            destination = new { location = new { latLng = new { latitude = last.Latitude!.Value, longitude = last.Longitude!.Value } } },
            intermediates,
            travelMode = "DRIVE",
            routingPreference = "TRAFFIC_AWARE",
            languageCode = "es-MX",
            units = "METRIC"
        };

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(15);

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://routes.googleapis.com/directions/v2:computeRoutes");
        req.Headers.Add("X-Goog-Api-Key", apiKey);
        req.Headers.Add("X-Goog-FieldMask", "routes.distanceMeters,routes.duration,routes.polyline.encodedPolyline");
        AddReferrer(req);
        req.Content = JsonContent.Create(body);

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("computeRoutes devolvió {Status}: {Body}", resp.StatusCode, errBody);
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
            var ds = (dur.GetString() ?? "0s").TrimEnd('s');
            double.TryParse(ds, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed);
            durationSeconds = (int)Math.Round(parsed);
        }

        string? polyline = null;
        if (route.TryGetProperty("polyline", out var poly) && poly.TryGetProperty("encodedPolyline", out var ep))
            polyline = ep.GetString();

        return (polyline, distanceMeters, durationSeconds);
    }

    /// <summary>
    /// La API key puede tener restricción de HTTP referrer: el servidor no envía Referer
    /// automáticamente. Workaround: inyectar el Referer desde config para que coincida con el
    /// dominio permitido. Solución definitiva: restricción por IP o ninguna en Google Cloud Console.
    /// </summary>
    private void AddReferrer(HttpRequestMessage req)
    {
        var frontendUrl = _config["App:FrontendUrl"];
        if (!string.IsNullOrEmpty(frontendUrl) && Uri.TryCreate(frontendUrl, UriKind.Absolute, out var refUri))
            req.Headers.Referrer = refUri;
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
