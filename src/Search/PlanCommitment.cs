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
    int Priority);
