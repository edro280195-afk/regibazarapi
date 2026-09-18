using EntregasApi.Services;
using Xunit;

namespace EntregasApi.Tests;

public class TandaTurnPlannerTests
{
    [Fact]
    public void ValidateCompleteAssignments_AcceptsEveryTurnExactlyOnce()
    {
        TandaTurnPlanner.ValidateCompleteAssignments(4, new[] { 3, 1, 4, 2 });
    }

    [Fact]
    public void ValidateCompleteAssignments_RejectsMissingOrDuplicateTurns()
    {
        Assert.Throws<InvalidOperationException>(() =>
            TandaTurnPlanner.ValidateCompleteAssignments(4, new[] { 1, 2, 2, 4 }));
    }

    [Fact]
    public void ValidateExactOrder_AcceptsSameParticipantsInDifferentOrder()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        TandaTurnPlanner.ValidateExactOrder(
            new[] { first, second },
            new[] { second, first });
    }

    [Fact]
    public void ValidateExactOrder_RejectsDuplicatesOrMissingParticipants()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        Assert.Throws<InvalidOperationException>(() =>
            TandaTurnPlanner.ValidateExactOrder(
                new[] { first, second },
                new[] { first, first }));
    }

    [Fact]
    public void ValidatePlaceAssignments_AcceptsArbitraryOrderAndEmptyPlaces()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        TandaTurnPlanner.ValidatePlaceAssignments(
            5,
            new[] { first, second },
            new[]
            {
                new TandaTurnPlanner.TandaPlaceAssignment(first, 4),
                new TandaTurnPlanner.TandaPlaceAssignment(second, 1)
            });
    }

    [Fact]
    public void ValidatePlaceAssignments_RejectsDuplicatePlaces()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        Assert.Throws<InvalidOperationException>(() =>
            TandaTurnPlanner.ValidatePlaceAssignments(
                5,
                new[] { first, second },
                new[]
                {
                    new TandaTurnPlanner.TandaPlaceAssignment(first, 2),
                    new TandaTurnPlanner.TandaPlaceAssignment(second, 2)
                }));
    }

    [Fact]
    public void ValidatePlaceAssignments_RejectsMissingOrOutOfRangeParticipants()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        Assert.Throws<InvalidOperationException>(() =>
            TandaTurnPlanner.ValidatePlaceAssignments(
                3,
                new[] { first, second },
                new[]
                {
                    new TandaTurnPlanner.TandaPlaceAssignment(first, 4)
                }));
    }
}
