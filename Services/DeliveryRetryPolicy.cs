using EntregasApi.Models;

namespace EntregasApi.Services;

/// <summary>
/// Centraliza la liberación de un pedido fallido y el reinicio de entregas que provienen
/// de flujos anteriores y aún tienen un registro operativo asociado.
/// </summary>
/// Cada reintento crea un registro nuevo y nunca borra la evidencia del intento cerrado.
public static class DeliveryRetryPolicy
{
    /// <summary>Libera el pedido de su ruta y lo deja disponible para programar un nuevo intento.</summary>
    public static void ReleaseForRetry(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);

        order.DeliveryRouteId = null;
        order.DeliveryRoute = null;
        order.Status = OrderStatus.Pending;
    }

    /// <summary>Crea un nuevo intento al asignar el pedido a una ruta.</summary>
    public static Delivery CreateForRoute(Order order, DeliveryRoute route, int sortOrder)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(route);

        order.DeliveryRoute = route;
        order.Status = OrderStatus.InRoute;

        return new Delivery
        {
            Order = order,
            Kind = DeliveryKind.Order,
            DeliveryRoute = route,
            SortOrder = sortOrder,
            Status = DeliveryStatus.Pending
        };
    }
}
