namespace CombatSolver;

internal readonly record struct SearchPassResult(
    SolverResult Result,
    SolverResult? TakeoverResult,
    bool Settled);
