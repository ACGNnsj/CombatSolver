namespace CombatSolver;

internal static partial class CombatSearchCoordinator
{
    private static SolverResult RunEarlyPotionPairRescue(SearchPassContext context, SolverResult selected)
    {
        CombatRootSnapshot root = context.Root;
        SolverDisplayNames displayNames = context.DisplayNames;
        BattleDamageSnapshot battleDamage = context.BattleDamage;
        SearchPolicySnapshot policy = context.Policy;
        CancellationToken cancellationToken = context.CancellationToken;
        Action<SolverProgress>? progressCallback = context.ProgressCallback;
        SearchBudgetLedger ledger = context.Budget;
        if (selected.ResultScope == SolverResultScope.SearchCompletion
            && selected.OnlyDeathRoutesFound
            && policy.PotionPolicy == SolverPotionPolicy.Smart
            && !policy.PotionStrategy.HasForcedDirectives
            && root.SearchablePotionCount >= 2)
        {
            CombatBeamSolver builder = new(root, displayNames, battleDamage,
                policy, cancellationToken, progressCallback, policy.Profile,
                potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                maximumPotionUses: 2);
            PlanAction[] openingPotions = builder.BuildOpeningPotionActions()
                .Where(action => action.Choice == null)
                .GroupBy(action => action.PotionSlot)
                .Take(2)
                .Select(group => group.First()).ToArray();
            if (openingPotions.Length == 2)
            {
                foreach (PlanAction[] prefix in new[]
                         {
                             openingPotions,
                             [openingPotions[1], openingPotions[0]],
                         })
                {
                    int remainingMilliseconds = ledger.RemainingRequestMilliseconds;
                    long remainingNodes = ledger.RemainingNodes(policy.Profile);
                    if (remainingMilliseconds <= 5_000 || remainingNodes <= 0)
                        break;
                    if (!builder.CanReplayOpeningPrefix(prefix))
                        continue;
                    SolverSearchProfile pairProfile = policy.Profile with
                    {
                        MaxExpandedNodes = (int)Math.Min(100_000, remainingNodes),
                        SoftTimeBudgetMilliseconds = Math.Min(30_000,
                            remainingMilliseconds - 2_000),
                    };
                    SolverResult pair = new CombatBeamSolver(root, displayNames,
                        battleDamage, policy, cancellationToken, progressCallback,
                        pairProfile, potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                        maximumPotionUses: 2, minimumPotionUses: 2,
                        fixedPrefixActions: prefix,
                        resetFixedPrefixSchedulingBaseline: true).Solve();
                    bool improved = pair.ResultScope == SolverResultScope.SearchCompletion
                        && IsBetterPotionPolicyResult(root, policy, pair, selected);
                    if (improved)
                        selected = pair;
                    policy.Diagnostics.Info($"[CombatSolver/Test] " +
                        $"EARLY_POTION_PAIR prefix={prefix[0].PotionId}+{prefix[1].PotionId} " +
                        $"won={IsCompleteVictory(pair)} " +
                        $"hp_lost={pair.ProjectedBattleHpLost} selected={improved}");
                }
            }
        }
        return selected;
    }
}
