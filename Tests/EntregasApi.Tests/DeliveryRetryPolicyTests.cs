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
    public void CreateForRoute_CreatesANewAttemptAndPreservesTheFailedAttempt()
    {
        var route = new DeliveryRoute { Id = 24, DriverToken = "new-route" };
        var order = new Order { Status = OrderStatus.NotDelivered };
        var failedDelivery = new Delivery
        {
            Status = DeliveryStatus.NotDelivered,
            FailureReason = "No contest" + (char)0x00f3,
            Notes = "Se llam" + (char)0x00f3 + " dos veces",
            DeliveredAt = DateTime.UtcNow,
            SignatureSvg = "<svg />",
            SignedByName = "Persona anterior",
            SignedAt = DateTime.UtcNow,
            ArrivedAt = DateTime.UtcNow
        };
        failedDelivery.Evidences.Add(new DeliveryEvidence
        {
            DeliveryId = 1,
            ImagePath = "evidencia.jpg",
            Type = EvidenceType.NonDeliveryProof
        });

        var retry = DeliveryRetryPolicy.CreateForRoute(order, route, 3);

        Assert.Same(route, order.DeliveryRoute);
        Assert.Equal(OrderStatus.InRoute, order.Status);
        Assert.Same(route, retry.DeliveryRoute);
        Assert.Equal(DeliveryStatus.Pending, retry.Status);
        Assert.Equal(3, retry.SortOrder);
        Assert.Same(order, retry.Order);
        Assert.Equal(DeliveryStatus.NotDelivered, failedDelivery.Status);
        Assert.Equal("No contest" + (char)0x00f3, failedDelivery.FailureReason);
        Assert.Equal("Se llam" + (char)0x00f3 + " dos veces", failedDelivery.Notes);
        Assert.NotNull(failedDelivery.DeliveredAt);
        Assert.NotNull(failedDelivery.SignatureSvg);
        Assert.NotNull(failedDelivery.SignedByName);
        Assert.NotNull(failedDelivery.SignedAt);
        Assert.Single(failedDelivery.Evidences);
    }
}
