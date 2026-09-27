namespace CombatSolver;

internal enum PlanCommitmentKind
{
    CopyPower,
    CopyCard,
    PowerCycle,
}

internal sealed record PlanCommitment(
    PlanCommitmentKind Kind,
    PlanAction[] Prefix,
    int OpenedTurn,
    string PayoffCardId,
    bool UsesPotion,
    int Priority)
{
    internal int CountRealizedPayoffPlays(SearchNode node)
    {
        int count = 0;
        for (SearchNode? cursor = node; cursor != null; cursor = cursor.Parent)
        {
            if (cursor.Action is { Kind: PlanActionKind.PlayCard } action
                && (Kind == PlanCommitmentKind.PowerCycle
                    ? action.Turn >= OpenedTurn : action.Turn > OpenedTurn)
                && string.Equals(action.CardId, PayoffCardId, StringComparison.Ordinal))
                count++;
        }
        return count;
    }
}
