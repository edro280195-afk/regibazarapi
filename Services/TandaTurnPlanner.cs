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

    public static void ValidatePlaceAssignments(
        int totalWeeks,
        IReadOnlyCollection<Guid> participantIds,
        IReadOnlyCollection<TandaPlaceAssignment> requestedAssignments)
    {
        if (participantIds.Count != requestedAssignments.Count
            || requestedAssignments.Select(a => a.ParticipantId).Distinct().Count() != requestedAssignments.Count
            || !participantIds.ToHashSet().SetEquals(requestedAssignments.Select(a => a.ParticipantId)))
        {
            throw new InvalidOperationException(
                "Los lugares enviados no coinciden con los participantes de la tanda.");
        }

        if (requestedAssignments.Any(a => a.AssignedTurn < 1 || a.AssignedTurn > totalWeeks))
        {
            throw new InvalidOperationException(
                $"Todos los lugares deben estar entre 1 y {totalWeeks}.");
        }

        if (requestedAssignments.Select(a => a.AssignedTurn).Distinct().Count() != requestedAssignments.Count)
        {
            throw new InvalidOperationException("No puede haber dos participantes en el mismo lugar.");
        }
    }

    public readonly record struct TandaPlaceAssignment(Guid ParticipantId, int AssignedTurn);
}
