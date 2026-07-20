using EntregasApi.Models;

namespace EntregasApi.Services;

/// <summary>
/// Centraliza la liberación de un pedido fallido y el reinicio de entregas que provienen
/// de flujos anteriores y aún tienen un registro operativo asociado.
/// </summary>
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

    /// <summary>Reinicia un registro operativo existente al asignarlo a una nueva ruta.</summary>
    public static void PrepareForRoute(Order order, Delivery delivery, DeliveryRoute route, int sortOrder)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(route);

        order.DeliveryRoute = route;
        order.Status = OrderStatus.InRoute;

        delivery.DeliveryRoute = route;
        delivery.Kind = DeliveryKind.Order;
        delivery.SortOrder = sortOrder;
        delivery.Status = DeliveryStatus.Pending;
        delivery.Notes = null;
        delivery.FailureReason = null;
        delivery.DeliveredAt = null;
        delivery.SignatureSvg = null;
        delivery.SignedByName = null;
        delivery.SignedAt = null;
        delivery.ArrivedAt = null;
    }
}
