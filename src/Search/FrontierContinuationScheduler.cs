using System.Text.Json;

namespace CombatSolver;

internal enum ContinuationPurpose
{
    EarlyPotionPair,
    EarlierCopyDelayedDamage,
    NoCostOpening,
    TurnEndChoice,
    TurnEndChoiceShorterOpening,
}

internal interface IFrontierContinuationSource
{
    bool DeduplicatePrefixes { get; }

    IEnumerable<PlanAction[]> Enumerate();

    bool CanReplay(PlanAction[] prefix);
}

internal sealed record ContinuationSearchRequest(
    SearchPassContext Context,
    ContinuationPurpose Purpose,
    PlanAction[] Prefix,
    SolverSearchProfile Profile,
    SolverPotionPolicy? PotionPolicyOverride,
    int? MaximumPotionUses,
    int? MinimumPotionUses);

internal sealed record ContinuationSearchOutcome(
    ContinuationSearchRequest Request,
    SolverResult Result);

internal sealed class FrontierContinuationScheduler(SearchPassContext context)
{
    private readonly HashSet<(ContinuationPurpose Purpose, string Prefix)> _seen = [];

    internal IEnumerable<ContinuationSearchOutcome> Run(
        IFrontierContinuationSource source,
        ContinuationPurpose purpose,
        int minimumRemainingMilliseconds,
        int maximumNodes,
        int maximumMilliseconds,
        int reserveMilliseconds,
        SolverPotionPolicy? potionPolicyOverride,
        Func<(int? Maximum, int? Minimum)> potionBounds)
    {
        foreach (PlanAction[] prefix in source.Enumerate())
        {
            SearchBudgetWindow window = context.Budget.RequestWindow(context.Policy.Profile);
            if (!window.CanStart(minimumRemainingMilliseconds))
                yield break;
            if (!source.CanReplay(prefix))
                continue;
            if (source.DeduplicatePrefixes
                && !_seen.Add((purpose, JsonSerializer.Serialize(prefix))))
                continue;
            (int? maximumPotionUses, int? minimumPotionUses) = potionBounds();
            ContinuationSearchRequest request = new(context, purpose, prefix,
                window.Limit(context.Policy.Profile, maximumNodes, maximumMilliseconds,
                    reserveMilliseconds), potionPolicyOverride, maximumPotionUses,
                minimumPotionUses);
            SolverResult result = new CombatBeamSolver(
                context.Root, context.DisplayNames, context.BattleDamage,
                context.Policy, context.CancellationToken, context.ProgressCallback,
                request.Profile, potionPolicyOverride: request.PotionPolicyOverride,
                maximumPotionUses: request.MaximumPotionUses,
                fixedPrefixActions: request.Prefix,
                resetFixedPrefixSchedulingBaseline: true,
                minimumPotionUses: request.MinimumPotionUses).Solve();
            yield return new(request, result);
        }
    }
}
