namespace EntregasApi.Services;

public static class TandaTurnPlanner
{
    public static void ValidateCompleteAssignments(
        int totalWeeks,
        IReadOnlyCollection<int> assignedTurns)
    {
        if (totalWeeks < 1)
        {
            throw new InvalidOperationException("La tanda debe tener al menos una semana.");
        }

        var expectedTurns = Enumerable.Range(1, totalWeeks).ToHashSet();
        if (assignedTurns.Count != totalWeeks
            || assignedTurns.Distinct().Count() != assignedTurns.Count
            || !expectedTurns.SetEquals(assignedTurns))
        {
            throw new InvalidOperationException(
                $"Debes asignar exactamente los lugares del 1 al {totalWeeks}.");
        }
    }

    public static void ValidateExactOrder(
        IReadOnlyCollection<Guid> participantIds,
        IReadOnlyCollection<Guid> requestedOrder)
    {
        if (participantIds.Count != requestedOrder.Count
            || requestedOrder.Distinct().Count() != requestedOrder.Count
            || !participantIds.ToHashSet().SetEquals(requestedOrder))
        {
            throw new InvalidOperationException(
                "La lista de participantes no coincide con los integrantes de la tanda.");
        }
    }
}
