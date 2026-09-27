namespace CombatSolver;

internal sealed class SearchRequestPipeline
{
    private readonly SearchPassContext _context;
    private readonly Func<SearchPassContext, SearchPassResult> _runPass;

    internal SearchRequestPipeline(
        SearchPassContext context,
        Func<SearchPassContext, SearchPassResult> runPass)
    {
        _context = context;
        _runPass = runPass;
    }

    internal SolverResult Run()
    {
        SearchPassResult lastPass = _runPass(_context);
        SolverResult result = lastPass.Result;
        if (lastPass.TakeoverResult != null)
            return lastPass.TakeoverResult;
        if (lastPass.Settled || _context.Policy.FixedBudget)
            return result;
        lastPass = CombatSearchCoordinator.EscalateSearchWhenNoVictory(
            _context,
            lastPass,
            _runPass,
            () => _context.CancellationToken.IsCancellationRequested
                || _context.Policy.Interaction?.CurrentTakeoverRequest != null);
        result = lastPass.Result;
        if (lastPass.TakeoverResult != null)
            return lastPass.TakeoverResult;
        if (lastPass.Settled)
            return result;
        _context.Policy.Diagnostics.Info(
            $"[CombatSolver/Test] SEARCH_SESSION mode=single_anytime " +
            $"total_budget_ms={_context.Profile.SoftTimeBudgetMilliseconds}");
        return result;
    }
}
