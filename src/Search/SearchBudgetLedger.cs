using System.Diagnostics;

namespace CombatSolver;

internal sealed class SearchBudgetLedger
{
    private readonly Stopwatch _requestClock;
    private readonly int _requestLimitMilliseconds;

    internal SearchBudgetLedger(Stopwatch requestClock, SearchPolicySnapshot policy)
    {
        _requestClock = requestClock;
        _requestLimitMilliseconds = policy.BudgetOverrideMilliseconds
            ?? policy.Profile.SoftTimeBudgetMilliseconds;
    }

    internal SearchRequestWorkTotals WorkTotals { get; } = new();

    internal int RemainingRequestMilliseconds
        => _requestLimitMilliseconds - (int)_requestClock.ElapsedMilliseconds;

    internal long RemainingRequestMillisecondsLong
        => _requestLimitMilliseconds - _requestClock.ElapsedMilliseconds;

    internal long RemainingNodes(SolverSearchProfile profile)
        => profile.MaxExpandedNodes - WorkTotals.Snapshot().ExpandedNodes;

    internal long RemainingMilliseconds(SolverSearchProfile profile, Stopwatch clock)
        => profile.SoftTimeBudgetMilliseconds - clock.ElapsedMilliseconds;
}
