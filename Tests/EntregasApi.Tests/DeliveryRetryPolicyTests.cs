using EntregasApi.Models;
using EntregasApi.Services;
using Xunit;

namespace EntregasApi.Tests;

public class DeliveryRetryPolicyTests
{
    [Fact]
    public void ReleaseForRetry_ClearsRouteAndReturnsOrderToPending()
    {
        var order = new Order
        {
            DeliveryRouteId = 12,
            DeliveryRoute = new DeliveryRoute { Id = 12, DriverToken = "route-token" },
            Status = OrderStatus.NotDelivered
        };

        DeliveryRetryPolicy.ReleaseForRetry(order);

        Assert.Null(order.DeliveryRouteId);
        Assert.Null(order.DeliveryRoute);
        Assert.Equal(OrderStatus.Pending, order.Status);
    }

    [Fact]
    public void PrepareForRoute_ResetsFailedOperationalStateWithoutDeletingEvidence()
    {
        var route = new DeliveryRoute { Id = 24, DriverToken = "new-route" };
        var order = new Order { Status = OrderStatus.NotDelivered };
        var delivery = new Delivery
        {
            Status = DeliveryStatus.NotDelivered,
            FailureReason = "No contestó",
            Notes = "Se llamó dos veces",
            DeliveredAt = DateTime.UtcNow,
            SignatureSvg = "<svg />",
            SignedByName = "Persona anterior",
            SignedAt = DateTime.UtcNow,
            ArrivedAt = DateTime.UtcNow
        };
        delivery.Evidences.Add(new DeliveryEvidence
        {
            DeliveryId = 1,
            ImagePath = "evidencia.jpg",
            Type = EvidenceType.NonDeliveryProof
        });

        DeliveryRetryPolicy.PrepareForRoute(order, delivery, route, 3);

        Assert.Same(route, order.DeliveryRoute);
        Assert.Equal(OrderStatus.InRoute, order.Status);
        Assert.Same(route, delivery.DeliveryRoute);
        Assert.Equal(DeliveryStatus.Pending, delivery.Status);
        Assert.Equal(3, delivery.SortOrder);
        Assert.Null(delivery.FailureReason);
        Assert.Null(delivery.Notes);
        Assert.Null(delivery.DeliveredAt);
        Assert.Null(delivery.SignatureSvg);
        Assert.Null(delivery.SignedByName);
        Assert.Null(delivery.SignedAt);
        Assert.Null(delivery.ArrivedAt);
        Assert.Single(delivery.Evidences);
    }
}
