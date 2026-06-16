using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using EntregasApi.Models;
using Microsoft.Extensions.Caching.Memory;

namespace EntregasApi.Services;

public class RouteOptimizerService : IRouteOptimizerService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<RouteOptimizerService> _logger;
    private readonly IMemoryCache _cache;

    // Routes API v2 acepta hasta 25 waypoints intermedios por solicitud de computeRoutes.
    private const int MaxIntermediatesForPolyline = 25;
    // computeRouteMatrix permite hasta 625 elementos (orígenes × destinos) por request.
    private const int MatrixElementBudget = 600;

    // Cache de tramos (origen→destino) para no re-consultar a Google en cada cambio de selección.
    private const string LegCachePrefix = "rom:leg:";
    private static readonly TimeSpan LegCacheTtl = TimeSpan.FromHours(6);

    public RouteOptimizerService(IHttpClientFactory httpFactory, IConfiguration config, ILogger<RouteOptimizerService> logger, IMemoryCache cache)
    {
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
        _cache = cache;
    }

    private static string LegKey((double lat, double lng) a, (double lat, double lng) b)
        => FormattableString.Invariant($"{LegCachePrefix}{a.lat:F6},{a.lng:F6}>{b.lat:F6},{b.lng:F6}");

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

        // ── Cache: rellenar tramos ya conocidos; solo llamamos a Google si falta alguno ──
        bool anyMissing = false;
        for (int i = 0; i < N; i++)
            for (int j = 0; j < N; j++)
            {
                if (i == j) { dur[i][j] = 0; dist[i][j] = 0; continue; }
                if (_cache.TryGetValue(LegKey(pts[i], pts[j]), out (double dur, double dist) leg))
                {
                    dur[i][j] = leg.dur;
                    dist[i][j] = leg.dist;
                }
                else anyMissing = true;
            }

        if (!anyMissing)
            return (dur, dist); // todo servido del cache — cero consultas a Google

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

        // Guardar todos los tramos en cache (incluye huecos estimados con Haversine).
        for (int i = 0; i < N; i++)
            for (int j = 0; j < N; j++)
            {
                if (i == j) continue;
                _cache.Set(LegKey(pts[i], pts[j]), (dur[i][j], dist[i][j]), LegCacheTtl);
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
    //  TSP de ruta abierta (depot fijo al inicio, final libre).
    //  Multi-arranque nearest-neighbor + búsqueda local (2-opt + Or-opt).
    //  Las mejoras se evalúan por costo TOTAL, así que sirve aunque la matriz
    //  sea asimétrica (en calles reales el tiempo de ida ≠ el de vuelta).
    // ───────────────────────────────────────────────────────────────────
    private static List<int> SolveOpenRoute(double[][] cost, int n)
    {
        // Semillas: NN clásico desde el depot + NN forzando distintos primeros nodos.
        var seeds = new List<int> { 0 };
        int maxStarts = Math.Min(n, 12);
        int stride = Math.Max(1, n / Math.Max(1, maxStarts));
        for (int k = 1; k <= n; k += stride) seeds.Add(k);

        List<int>? best = null;
        double bestCost = double.MaxValue;

        foreach (var seed in seeds)
        {
            var path = NearestNeighborSeed(cost, n, seed);
            LocalSearch(path, cost);
            double c = OpenPathCost(path, cost);
            if (c < bestCost) { bestCost = c; best = path; }
        }

        return best ?? new List<int>();
    }

    /// <summary>NN abierto. forcedFirst 1..n fuerza ese nodo como primera parada; 0 = NN normal.</summary>
    private static List<int> NearestNeighborSeed(double[][] cost, int n, int forcedFirst)
    {
        var path = new List<int>(n);
        var visited = new bool[n + 1];
        visited[0] = true;
        int current = 0;

        if (forcedFirst >= 1 && forcedFirst <= n)
        {
            path.Add(forcedFirst);
            visited[forcedFirst] = true;
            current = forcedFirst;
        }

        while (path.Count < n)
        {
            int best = -1;
            double bestC = double.MaxValue;
            for (int j = 1; j <= n; j++)
            {
                if (visited[j]) continue;
                if (cost[current][j] < bestC) { bestC = cost[current][j]; best = j; }
            }
            if (best < 0) break;
            path.Add(best);
            visited[best] = true;
            current = best;
        }
        return path;
    }

    /// <summary>Costo de la ruta abierta depot(0) → path[0] → … → path[^1] (sin regreso).</summary>
    private static double OpenPathCost(List<int> path, double[][] cost)
    {
        double sum = 0;
        int prev = 0;
        foreach (var idx in path) { sum += cost[prev][idx]; prev = idx; }
        return sum;
    }

    /// <summary>Costo total recorriendo la lista completa (full[0] = depot).</summary>
    private static double FullCost(List<int> full, double[][] cost)
    {
        double sum = 0;
        for (int k = 0; k + 1 < full.Count; k++) sum += cost[full[k]][full[k + 1]];
        return sum;
    }

    /// <summary>Búsqueda local: alterna barridos 2-opt y Or-opt hasta que ninguno mejore.</summary>
    private static void LocalSearch(List<int> path, double[][] cost)
    {
        var full = new List<int>(path.Count + 1) { 0 };
        full.AddRange(path);

        bool improved = true;
        int guard = 0;
        while (improved && guard++ < 100)
        {
            improved = false;
            if (TwoOptPass(full, cost)) improved = true;
            if (OrOptPass(full, cost)) improved = true;
        }

        path.Clear();
        for (int k = 1; k < full.Count; k++) path.Add(full[k]);
    }

    /// <summary>Un barrido 2-opt: revierte un segmento si baja el costo total (depot fijo en 0).</summary>
    private static bool TwoOptPass(List<int> full, double[][] cost)
    {
        bool improved = false;
        double baseCost = FullCost(full, cost);
        for (int i = 1; i < full.Count - 1; i++)
        {
            for (int j = i + 1; j < full.Count; j++)
            {
                full.Reverse(i, j - i + 1);
                double c = FullCost(full, cost);
                if (c + 1e-9 < baseCost) { baseCost = c; improved = true; }
                else full.Reverse(i, j - i + 1); // revertir
            }
        }
        return improved;
    }

    /// <summary>Un barrido Or-opt: reubica segmentos de 1..3 paradas a una mejor posición.</summary>
    private static bool OrOptPass(List<int> full, double[][] cost)
    {
        double baseCost = FullCost(full, cost);
        for (int segLen = 1; segLen <= 3; segLen++)
        {
            for (int segStart = 1; segStart + segLen <= full.Count; segStart++)
            {
                var seg = full.GetRange(segStart, segLen);
                var rest = new List<int>(full);
                rest.RemoveRange(segStart, segLen);

                for (int pos = 0; pos < rest.Count; pos++)
                {
                    var cand = new List<int>(rest);
                    cand.InsertRange(pos + 1, seg); // pos≥0 ⇒ inserta en índice ≥1, el depot queda en 0
                    double c = FullCost(cand, cost);
                    if (c + 1e-9 < baseCost)
                    {
                        full.Clear();
                        full.AddRange(cand);
                        return true;
                    }
                }
            }
        }
        return false;
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
