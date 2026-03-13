using EntregasApi.Models;

namespace EntregasApi.Services;

public interface IRouteOptimizerService
{
    /// <summary>
    /// Optimiza el orden de una lista de órdenes basándose en su ubicación geográfica.
    /// </summary>
    /// <param name="orders">Lista de órdenes a optimizar.</param>
    /// <param name="startLat">Latitud de inicio (ej: ubicación del negocio o del repartidor).</param>
    /// <param name="startLng">Longitud de inicio.</param>
    /// <returns>Lista de órdenes en el orden óptimo de entrega.</returns>
    List<Order> OptimizeRoute(List<Order> orders, double startLat, double startLng);
}
