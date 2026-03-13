using EntregasApi.Models;

namespace EntregasApi.Services;

public class RouteOptimizerService : IRouteOptimizerService
{
    public List<Order> OptimizeRoute(List<Order> orders, double startLat, double startLng)
    {
        if (orders == null || !orders.Any())
            return new List<Order>();

        // Separamos órdenes con coordenadas de las que no tienen
        var withCoords = orders.Where(o => o.Client?.Latitude != null && o.Client?.Longitude != null).ToList();
        var withoutCoords = orders.Where(o => o.Client?.Latitude == null || o.Client?.Longitude == null).ToList();

        var optimized = new List<Order>();
        var remaining = new List<Order>(withCoords);
        
        double currentLat = startLat;
        double currentLng = startLng;

        // Algoritmo Nearest Neighbor
        while (remaining.Any())
        {
            Order? nearest = null;
            double minDistance = double.MaxValue;
            int nearestIdx = -1;

            for (int i = 0; i < remaining.Count; i++)
            {
                var order = remaining[i];
                double dist = CalculateDistance(currentLat, currentLng, order.Client.Latitude!.Value, order.Client.Longitude!.Value);
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

        // Agregamos las órdenes sin coordenadas al final
        optimized.AddRange(withoutCoords);

        return optimized;
    }

    /// <summary>
    /// Calcula la distancia entre dos puntos usando la fórmula de Haversine (en km).
    /// </summary>
    private double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371; // Radio de la Tierra en km
        double dLat = ToRadians(lat2 - lat1);
        double dLon = ToRadians(lon2 - lon1);
        
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }

    private double ToRadians(double angle) => Math.PI * angle / 180.0;
}
