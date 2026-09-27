using System.Diagnostics;

namespace CombatSolver;

internal sealed record SearchPassContext(
    CombatRootSnapshot Root,
    SolverDisplayNames DisplayNames,
    BattleDamageSnapshot BattleDamage,
    SearchPolicySnapshot Policy,
    SolverSearchProfile Profile,
    Stopwatch Clock,
    SearchBudgetLedger Budget,
    CancellationToken CancellationToken,
    Action<SolverProgress>? ProgressCallback,
    Action<SolverResult>? InterimResultCallback)
{
    internal long RemainingMilliseconds => Budget.RemainingMilliseconds(Profile, Clock);

    internal SearchBudgetWindow SliceWindow => Budget.PassWindow(Profile, Clock);
}
