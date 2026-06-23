using EntregasApi.Models;

namespace EntregasApi.Services;

public class RouteOptimizerService : IRouteOptimizerService
{
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
