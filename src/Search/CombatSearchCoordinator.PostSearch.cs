using MegaCrit.Sts2.Core.Rooms;

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
                    SearchBudgetWindow window = ledger.RequestWindow(policy.Profile);
                    if (!window.CanStart(5_000))
                        break;
                    if (!builder.CanReplayOpeningPrefix(prefix))
                        continue;
                    SolverSearchProfile pairProfile = window.Limit(policy.Profile,
                        maximumNodes: 100_000, maximumMilliseconds: 30_000,
                        reserveMilliseconds: 2_000);
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

    private static SolverResult RunForcedPotionOpeningRescue(SearchPassContext context, SolverResult selected)
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
            && policy.PotionStrategy.HasForcedDirectives
            && policy.PotionStrategy.Directives
                .Where(directive => directive.Directive == SolverPotionDirective.Force)
                .All(directive => !PotionUsePolicy.RequiresOpeningUse(directive.PotionId)))
        {
            PotionFreePolicyBaseline forcedBaseline = new(
                false, StrategicHpDeficit(root, policy, selected),
                selected.Snapshot.PlayerHp, selected.CombatEndedTurn)
            {
                DeathSaveUseCount = selected.Snapshot.ProjectedDeathSaveUseCount,
            };
            HashSet<string> attemptedOpenings = [];
            for (int variant = 0; variant < 3; variant++)
            {
                int remainingMilliseconds = ledger.RemainingRequestMilliseconds;
                long remainingNodes = ledger.RemainingNodes(policy.Profile);
                if (remainingMilliseconds <= 30_000 || remainingNodes <= 0)
                    break;
                SolverSearchProfile discoveryProfile = policy.Profile with
                {
                    MaxExpandedNodes = (int)Math.Min(40_000, remainingNodes),
                    SoftTimeBudgetMilliseconds = Math.Min(12_000, remainingMilliseconds - 2_000),
                    SecondRankBand = variant == 1,
                    BaseScoreOnly = variant == 2,
                    AggressivePowerCommitment = variant == 0,
                };
                SolverResult discovery = new CombatBeamSolver(root, displayNames,
                    battleDamage, policy, cancellationToken, progressCallback,
                    discoveryProfile, potionPolicyOverride: SolverPotionPolicy.Disabled,
                    maximumPotionUses: 0).Solve();
                PlanAction[] opening = discovery.BestNode.Actions
                    .TakeWhile(action => action.Turn == root.StartTurnNumber)
                    .ToArray();
                if (opening.LastOrDefault()?.Kind != PlanActionKind.EndTurn)
                    continue;
                CombatBeamSolver targetBuilder = new(root, displayNames,
                    battleDamage, policy, cancellationToken, progressCallback,
                    discoveryProfile, potionPolicyOverride: SolverPotionPolicy.Disabled,
                    maximumPotionUses: 0);
                IReadOnlyList<PlanAction[]> targetVariants = targetBuilder
                    .BuildOpeningFocusedTargetPrefixes(opening);
                foreach (PlanAction[] focusedOpening in new[] { opening }
                             .Concat(targetVariants))
                {
                    if (!attemptedOpenings.Add(PowerPrefixKey(focusedOpening)))
                        continue;
                    remainingMilliseconds = ledger.RemainingRequestMilliseconds;
                    remainingNodes = ledger.RemainingNodes(policy.Profile);
                    if (remainingMilliseconds <= 20_000 || remainingNodes <= 0)
                        break;
                    SolverSearchProfile continuationProfile = policy.Profile with
                    {
                        MaxExpandedNodes = (int)Math.Min(60_000, remainingNodes),
                        SoftTimeBudgetMilliseconds = Math.Min(18_000, remainingMilliseconds - 2_000),
                    };
                    int forcedPotionCount = policy.PotionStrategy.ForcedDirectiveCount;
                    int potionCount = Math.Min(root.SearchablePotionCount,
                        forcedPotionCount + 1);
                    SolverResult continuation;
                    try
                    {
                        continuation = new CombatBeamSolver(root, displayNames,
                            battleDamage, policy, cancellationToken,
                            progressCallback, continuationProfile,
                            potionPolicyOverride: potionCount > forcedPotionCount
                                ? SolverPotionPolicy.RequireAtLeastOne : null,
                            potionFreePolicyBaseline: forcedBaseline,
                            maximumPotionUses: potionCount,
                            fixedPrefixActions: focusedOpening,
                            resetFixedPrefixSchedulingBaseline: true,
                            minimumPotionUses: potionCount,
                            earliestPotionTurn: root.EncounterRoomType == RoomType.Boss
                                ? root.StartTurnNumber
                                    + SolverWeights.BossEnemyStrengthSuppressionHorizon / 2 + 1
                                : null).Solve();
                    }
                    catch (PotionPolicyUnsatisfiedException)
                    {
                        continue;
                    }
                    bool improved = continuation.ResultScope == SolverResultScope.SearchCompletion
                        && policy.PotionStrategy.EvaluateForcedUses(
                            continuation.BestNode.Actions, renewablePotionShapedRock: false)
                            .AllForcedUsesSatisfied
                        && IsBetterPotionPolicyResult(root, policy, continuation, selected);
                    policy.Diagnostics.Info($"[CombatSolver/Test] FORCED_POTION_OPENING_RESCUE " +
                        $"opening={string.Join('+', focusedOpening.Select(action =>
                            $"{action.CardId}:{action.TargetCombatId}"))} " +
                        $"won={IsCompleteVictory(continuation)} " +
                        $"hp_lost={continuation.ProjectedBattleHpLost} " +
                        $"potions={continuation.PotionCount} " +
                        $"selected={improved}");
                    if (improved)
                        selected = continuation;
                    if (IsCompleteVictory(continuation))
                        continue;
                    PlanAction[] nextTurn = continuation.BestNode.Actions
                        .Where(action => action.Turn == root.StartTurnNumber + 1)
                        .Take(2).ToArray();
                    if (nextTurn.Length != 2
                        || nextTurn.Any(action => action.Kind != PlanActionKind.PlayCard))
                        continue;
                    PlanAction[] reordered = [.. focusedOpening, nextTurn[1], nextTurn[0]];
                    if (!targetBuilder.CanReplayOpeningPrefix(reordered))
                        continue;
                    remainingMilliseconds = ledger.RemainingRequestMilliseconds;
                    remainingNodes = ledger.RemainingNodes(policy.Profile);
                    if (remainingMilliseconds <= 20_000 || remainingNodes <= 0)
                        break;
                    SolverSearchProfile reorderedProfile = continuationProfile with
                    {
                        MaxExpandedNodes = (int)Math.Min(60_000, remainingNodes),
                        SoftTimeBudgetMilliseconds = Math.Min(18_000, remainingMilliseconds - 2_000),
                    };
                    SolverResult reorderedResult;
                    try
                    {
                        reorderedResult = new CombatBeamSolver(root, displayNames,
                            battleDamage, policy, cancellationToken,
                            progressCallback, reorderedProfile,
                            potionPolicyOverride: potionCount > forcedPotionCount
                                ? SolverPotionPolicy.RequireAtLeastOne : null,
                            potionFreePolicyBaseline: forcedBaseline,
                            maximumPotionUses: potionCount,
                            fixedPrefixActions: reordered,
                            resetFixedPrefixSchedulingBaseline: true,
                            minimumPotionUses: potionCount,
                            earliestPotionTurn: root.EncounterRoomType == RoomType.Boss
                                ? root.StartTurnNumber
                                    + SolverWeights.BossEnemyStrengthSuppressionHorizon / 2 + 1
                                : null).Solve();
                    }
                    catch (PotionPolicyUnsatisfiedException)
                    {
                        continue;
                    }
                    bool reorderedImproved = reorderedResult.ResultScope
                            == SolverResultScope.SearchCompletion
                        && policy.PotionStrategy.EvaluateForcedUses(
                            reorderedResult.BestNode.Actions, renewablePotionShapedRock: false)
                            .AllForcedUsesSatisfied
                        && IsBetterPotionPolicyResult(root, policy, reorderedResult, selected);
                    policy.Diagnostics.Info($"[CombatSolver/Test] FORCED_POTION_TURN_ORDER " +
                        $"prefix={nextTurn[1].CardId}+{nextTurn[0].CardId} " +
                        $"won={IsCompleteVictory(reorderedResult)} " +
                        $"hp_lost={reorderedResult.ProjectedBattleHpLost} " +
                        $"potions={reorderedResult.PotionCount} selected={reorderedImproved}");
                    if (reorderedImproved)
                        selected = reorderedResult;
                }
            }
        }
        return selected;
    }

    private static SolverResult RunTurnBoundaryRescue(
        SearchPassContext context,
        SolverResult selected,
        List<PlanAction[]> firstTurnAnchors)
    {
        CombatRootSnapshot root = context.Root;
        SolverDisplayNames displayNames = context.DisplayNames;
        BattleDamageSnapshot battleDamage = context.BattleDamage;
        SearchPolicySnapshot policy = context.Policy;
        CancellationToken cancellationToken = context.CancellationToken;
        Action<SolverProgress>? progressCallback = context.ProgressCallback;
        SearchBudgetLedger ledger = context.Budget;
        bool rescueAfterDeath = selected.OnlyDeathRoutesFound
            && root.SearchablePotionCount > 0;
        bool refineNearZeroLoss = IsCompleteVictory(selected)
            && selected.ExplicitPotionCount == 0
            && selected.ProjectedBattleHpLost is > 0 and <= SolverWeights.PotionMinimumHpSaved;
        if (selected.ResultScope == SolverResultScope.SearchCompletion
            && policy.PotionPolicy == SolverPotionPolicy.Smart
            && !policy.PotionStrategy.HasForcedDirectives
            && (rescueAfterDeath || refineNearZeroLoss))
        {
            // Revisit distinct first-turn states while the shared request still has time and nodes.
            foreach (PlanAction[] firstTurn in firstTurnAnchors
                         .DistinctBy(PowerPrefixKey).Take(8))
            {
                if (firstTurn.LastOrDefault()?.Kind != PlanActionKind.EndTurn)
                    continue;
                int remainingMilliseconds = ledger.RemainingRequestMilliseconds;
                long remainingNodes = ledger.RemainingNodes(policy.Profile);
                if (remainingMilliseconds <= 5_000 || remainingNodes <= 0)
                    break;
                SolverSearchProfile rescueProfile = policy.Profile with
                {
                    MaxExpandedNodes = (int)Math.Min(80_000, remainingNodes),
                    SoftTimeBudgetMilliseconds = Math.Min(20_000, remainingMilliseconds - 2_000),
                };
                SolverResult? rescue = SolveOptionalPotionPosterior(
                    new CombatBeamSolver(root, displayNames, battleDamage,
                        policy, cancellationToken, progressCallback, rescueProfile,
                        potionPolicyOverride: rescueAfterDeath
                            ? SolverPotionPolicy.RequireAtLeastOne : SolverPotionPolicy.Disabled,
                        maximumPotionUses: rescueAfterDeath
                            ? MaximumSmartPotionUses(root, policy, potionFreeWon: false,
                                potionFreeHpDeficit: 0)
                            : 0,
                        fixedPrefixActions: firstTurn,
                        resetFixedPrefixSchedulingBaseline: true),
                    policy, "TURN_BOUNDARY_RESCUE");
                if (rescue == null)
                    continue;
                policy.Diagnostics.Info($"[CombatSolver/Test] TURN_BOUNDARY_RESCUE " +
                    $"won={IsCompleteVictory(rescue)} hp_lost={rescue.ProjectedBattleHpLost} " +
                    $"potions={rescue.PotionCount} first_turn={string.Join('+', firstTurn.Select(action => action.CardId))}");
                if (rescue.ResultScope == SolverResultScope.SearchCompletion
                    && IsBetterPotionPolicyResult(root, policy, rescue, selected))
                    selected = rescue;
            }
            if (refineNearZeroLoss && selected.ProjectedBattleHpLost > 0)
            {
                CombatBeamSolver NewPrefixBuilder() => new(root, displayNames, battleDamage,
                    policy, cancellationToken, progressCallback, policy.Profile,
                    potionPolicyOverride: SolverPotionPolicy.Disabled,
                    maximumPotionUses: 0);
                int continuationAttempts = 0;
                foreach (PlanAction freeAction in NewPrefixBuilder().BuildOpeningFreeOffensiveActions())
                {
                    foreach (PlanAction[] firstTurn in firstTurnAnchors
                                 .DistinctBy(PowerPrefixKey).Take(4))
                    {
                        if (firstTurn.LastOrDefault()?.Kind != PlanActionKind.EndTurn)
                            continue;
                        int remainingMilliseconds = ledger.RemainingRequestMilliseconds;
                        long remainingNodes = ledger.RemainingNodes(policy.Profile);
                        if (remainingMilliseconds <= 5_000 || remainingNodes <= 0)
                            break;
                        PlanAction[] combinedPrefix = [freeAction, .. firstTurn];
                        if (!NewPrefixBuilder().CanReplayOpeningPrefix(combinedPrefix))
                            continue;
                        SolverSearchProfile prefixProfile = policy.Profile with
                        {
                            MaxExpandedNodes = (int)Math.Min(30_000, remainingNodes),
                            SoftTimeBudgetMilliseconds = Math.Min(10_000, remainingMilliseconds - 2_000),
                        };
                        SolverResult freeRescue = new CombatBeamSolver(root, displayNames,
                            battleDamage, policy, cancellationToken, progressCallback,
                            prefixProfile, potionPolicyOverride: SolverPotionPolicy.Disabled,
                            maximumPotionUses: 0,
                            fixedPrefixActions: combinedPrefix,
                            resetFixedPrefixSchedulingBaseline: true).Solve();
                        if (freeRescue.ResultScope == SolverResultScope.SearchCompletion
                            && IsBetterPotionPolicyResult(root, policy, freeRescue, selected))
                            selected = freeRescue;
                        if (selected.ProjectedBattleHpLost == 0)
                            break;
                        PlanAction[] nextTurnPrefix = combinedPrefix;
                        for (int defenseCount = 0; defenseCount < 2; defenseCount++)
                        {
                            PlanAction? nextDefense = NewPrefixBuilder()
                                .BuildOpeningDefensiveFollowUp(nextTurnPrefix);
                            if (nextDefense == null)
                                break;
                            nextTurnPrefix = [.. nextTurnPrefix, nextDefense];
                            foreach (PlanAction nextAttack in NewPrefixBuilder()
                                         .BuildOpeningOffensiveCardVariantsAfterPrefix(nextTurnPrefix).Take(3))
                            {
                                remainingMilliseconds = ledger.RemainingRequestMilliseconds;
                                remainingNodes = ledger.RemainingNodes(policy.Profile);
                                if (remainingMilliseconds <= 5_000 || remainingNodes <= 0)
                                    break;
                                SolverSearchProfile continuationProfile = prefixProfile with
                                {
                                    MaxExpandedNodes = (int)Math.Min(30_000, remainingNodes),
                                    SoftTimeBudgetMilliseconds = Math.Min(
                                        10_000, remainingMilliseconds - 2_000),
                                };
                                SolverResult continuation = new CombatBeamSolver(root, displayNames,
                                    battleDamage, policy, cancellationToken, progressCallback,
                                    continuationProfile, potionPolicyOverride: SolverPotionPolicy.Disabled,
                                    maximumPotionUses: 0,
                                    fixedPrefixActions: [.. nextTurnPrefix, nextAttack,
                                        new PlanAction(PlanActionKind.EndTurn, root.StartTurnNumber + 1)],
                                    resetFixedPrefixSchedulingBaseline: true).Solve();
                                if (continuation.ResultScope == SolverResultScope.SearchCompletion
                                    && IsBetterPotionPolicyResult(root, policy, continuation, selected))
                                    selected = continuation;
                                if (selected.ProjectedBattleHpLost == 0
                                    || ++continuationAttempts >= 8)
                                    break;
                            }
                            if (selected.ProjectedBattleHpLost == 0 || continuationAttempts >= 8)
                                break;
                        }
                        if (selected.ProjectedBattleHpLost == 0 || continuationAttempts >= 8)
                            break;
                    }
                    if (selected.ProjectedBattleHpLost == 0 || continuationAttempts >= 8)
                        break;
                }
            }
        }
        return selected;
    }

    private static SolverResult RunZeroCostOpeningRescue(SearchPassContext context, SolverResult selected)
    {
        CombatRootSnapshot root = context.Root;
        SolverDisplayNames displayNames = context.DisplayNames;
        BattleDamageSnapshot battleDamage = context.BattleDamage;
        SearchPolicySnapshot policy = context.Policy;
        CancellationToken cancellationToken = context.CancellationToken;
        Action<SolverProgress>? progressCallback = context.ProgressCallback;
        SearchBudgetLedger ledger = context.Budget;
        if (selected.ResultScope == SolverResultScope.SearchCompletion
            && policy.PotionPolicy == SolverPotionPolicy.Smart
            && !policy.PotionStrategy.HasForcedDirectives
            && !policy.IncludeTurnSetup
            && IsCompleteVictory(selected)
            && selected.ExplicitPotionCount == 0
            && selected.ProjectedBattleHpLost > 0
            && selected.BestNode.Actions.FirstOrDefault()?.Kind == PlanActionKind.EndTurn)
        {
            CombatBeamSolver builder = new(root, displayNames, battleDamage,
                policy, cancellationToken, progressCallback, policy.Profile,
                potionPolicyOverride: SolverPotionPolicy.Disabled, maximumPotionUses: 0);
            int attempts = 0;
            if (ledger.RemainingRequestMillisecondsLong > 5_000)
            {
                foreach (PlanAction[] prefix in builder.BuildOpeningNoCostPrefixes())
                {
                    int remainingMilliseconds = ledger.RemainingRequestMilliseconds;
                    long remainingNodes = ledger.RemainingNodes(policy.Profile);
                    if (remainingMilliseconds <= 5_000 || remainingNodes <= 0)
                        break;
                    SolverSearchProfile prefixProfile = policy.Profile with
                    {
                        MaxExpandedNodes = (int)Math.Min(40_000, remainingNodes),
                        SoftTimeBudgetMilliseconds = Math.Min(12_000, remainingMilliseconds - 2_000),
                    };
                    SolverResult candidate = new CombatBeamSolver(root, displayNames,
                        battleDamage, policy, cancellationToken, progressCallback,
                        prefixProfile, potionPolicyOverride: SolverPotionPolicy.Disabled,
                        maximumPotionUses: 0, fixedPrefixActions: prefix,
                        resetFixedPrefixSchedulingBaseline: true).Solve();
                    bool improved = candidate.ResultScope == SolverResultScope.SearchCompletion
                        && IsBetterPotionPolicyResult(root, policy, candidate, selected);
                    if (improved)
                        selected = candidate;
                    policy.Diagnostics.Info($"[CombatSolver/Test] NO_COST_OPENING " +
                        $"prefix={string.Join('+', prefix.Select(action => action.CardId))} " +
                        $"hp_lost={candidate.ProjectedBattleHpLost} selected={improved}");
                    if (selected.ProjectedBattleHpLost == 0 || ++attempts >= 8)
                        break;
                }
            }
        }
        return selected;
    }

    private static SolverResult RunMidCombatRefinement(SearchPassContext context, SolverResult selected)
    {
        CombatRootSnapshot root = context.Root;
        SolverDisplayNames displayNames = context.DisplayNames;
        BattleDamageSnapshot battleDamage = context.BattleDamage;
        SearchPolicySnapshot policy = context.Policy;
        CancellationToken cancellationToken = context.CancellationToken;
        Action<SolverProgress>? progressCallback = context.ProgressCallback;
        SearchBudgetLedger ledger = context.Budget;
        if (selected.ResultScope == SolverResultScope.SearchCompletion
            && policy.PotionPolicy == SolverPotionPolicy.Smart
            && !policy.PotionStrategy.HasForcedDirectives
            && IsCompleteVictory(selected)
            && selected.ProjectedBattleHpLost > 0
            && selected.ExplicitPotionCount > 0
            && selected.CombatEndedTurn >= root.StartTurnNumber + 6)
        {
            PlanAction[] prefix = selected.BestNode.Actions
                .TakeWhile(action => action.Turn < root.StartTurnNumber + 3)
                .ToArray();
            int remainingMilliseconds = ledger.RemainingRequestMilliseconds;
            long remainingNodes = ledger.RemainingNodes(policy.Profile);
            if (prefix.LastOrDefault()?.Kind == PlanActionKind.EndTurn
                && prefix.All(action => action.Kind != PlanActionKind.UsePotion)
                && remainingMilliseconds > 20_000 && remainingNodes > 0)
            {
                CombatBeamSolver prefixBuilder = new(root, displayNames, battleDamage,
                    policy, cancellationToken, progressCallback, policy.Profile,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: selected.ExplicitPotionCount);
                SolverSearchProfile refinementProfile = policy.Profile with
                {
                    MaxExpandedNodes = (int)Math.Min(50_000, remainingNodes),
                    SoftTimeBudgetMilliseconds = Math.Min(15_000, remainingMilliseconds - 2_000),
                };
                SolverResult? refined = SolveOptionalPotionPosterior(
                    new CombatBeamSolver(root, displayNames, battleDamage, policy,
                        cancellationToken, progressCallback, refinementProfile,
                        potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                        maximumPotionUses: selected.ExplicitPotionCount,
                        minimumPotionUses: selected.ExplicitPotionCount,
                        fixedPrefixActions: prefix,
                        resetFixedPrefixSchedulingBaseline: true),
                    policy, "MID_COMBAT_REFINEMENT");
                if (refined?.ResultScope == SolverResultScope.SearchCompletion)
                {
                    bool improved = refined.ProjectedBattleHpLost < selected.ProjectedBattleHpLost
                        && IsBetterPotionPolicyResult(root, policy, refined, selected);
                    policy.Diagnostics.Info($"[CombatSolver/Test] MID_COMBAT_REFINEMENT " +
                        $"hp_lost={refined.ProjectedBattleHpLost} " +
                        $"potions={refined.PotionCount} selected={improved}");
                    if (improved)
                        selected = refined;
                }

                PlanAction[] fourthTurn = selected.BestNode.Actions
                    .TakeWhile(action => action.Turn <= root.StartTurnNumber + 3)
                    .ToArray();
                remainingMilliseconds = ledger.RemainingRequestMilliseconds;
                remainingNodes = ledger.RemainingNodes(policy.Profile);
                if (fourthTurn.Length > prefix.Length + 1
                    && fourthTurn.Last().Kind == PlanActionKind.EndTurn
                    && fourthTurn.All(action => action.Kind != PlanActionKind.UsePotion)
                    && fourthTurn[^2].Kind == PlanActionKind.PlayCard
                    && remainingMilliseconds > 18_000 && remainingNodes > 0)
                {
                    PlanAction[] beforeEndTurn = fourthTurn[..^1];
                    PlanAction? followUp = prefixBuilder
                        .BuildFreeOffensiveActionsAfterPrefix(beforeEndTurn)
                        .FirstOrDefault(action => action.CardId == beforeEndTurn[^1].CardId);
                    if (followUp != null)
                    {
                        PlanAction[] freePrefix = [.. beforeEndTurn, followUp, fourthTurn[^1]];
                        remainingMilliseconds = ledger.RemainingRequestMilliseconds;
                        remainingNodes = ledger.RemainingNodes(policy.Profile);
                        if (remainingMilliseconds > 18_000 && remainingNodes > 0
                            && prefixBuilder.CanReplayOpeningPrefix(freePrefix))
                        {
                            SolverSearchProfile freeProfile = policy.Profile with
                            {
                                MaxExpandedNodes = (int)Math.Min(50_000, remainingNodes),
                                SoftTimeBudgetMilliseconds = Math.Min(15_000, remainingMilliseconds - 2_000),
                            };
                            SolverResult? freeRefined = SolveOptionalPotionPosterior(
                                new CombatBeamSolver(root, displayNames, battleDamage, policy,
                                    cancellationToken, progressCallback, freeProfile,
                                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                                    maximumPotionUses: selected.ExplicitPotionCount,
                                    minimumPotionUses: selected.ExplicitPotionCount,
                                    fixedPrefixActions: freePrefix,
                                    resetFixedPrefixSchedulingBaseline: true),
                                policy, "MID_COMBAT_FREE_FOLLOW_UP");
                            if (freeRefined?.ResultScope == SolverResultScope.SearchCompletion)
                            {
                                bool improved = freeRefined.ProjectedBattleHpLost < selected.ProjectedBattleHpLost
                                    && IsBetterPotionPolicyResult(root, policy, freeRefined, selected);
                                policy.Diagnostics.Info($"[CombatSolver/Test] MID_COMBAT_FREE_FOLLOW_UP " +
                                    $"hp_lost={freeRefined.ProjectedBattleHpLost} " +
                                    $"potions={freeRefined.PotionCount} selected={improved}");
                                if (improved)
                                    selected = freeRefined;
                            }
                        }
                    }
                }
            }
        }
        return selected;
    }

    private static SolverResult RunTurnEndChoicePosterior(SearchPassContext context, SolverResult selected)
    {
        CombatRootSnapshot root = context.Root;
        SolverDisplayNames displayNames = context.DisplayNames;
        BattleDamageSnapshot battleDamage = context.BattleDamage;
        SearchPolicySnapshot policy = context.Policy;
        CancellationToken cancellationToken = context.CancellationToken;
        Action<SolverProgress>? progressCallback = context.ProgressCallback;
        SearchBudgetLedger ledger = context.Budget;
        if (selected.ResultScope == SolverResultScope.SearchCompletion
            && policy.PotionPolicy == SolverPotionPolicy.Smart
            && !policy.PotionStrategy.HasForcedDirectives
            && IsCompleteVictory(selected)
            && selected.ProjectedBattleHpLost > 0
            && selected.ExplicitPotionCount == 0)
        {
            PlanAction[] firstTurn = selected.BestNode.Actions
                .TakeWhile(action => action.Turn == root.StartTurnNumber)
                .ToArray();
            if (firstTurn.LastOrDefault() is
                { Kind: PlanActionKind.EndTurn, TurnStartChoices: { Count: > 0 } } chosenEndTurn
                && ledger.RemainingRequestMillisecondsLong > 25_000)
            {
                CombatBeamSolver choiceBuilder = new(root, displayNames, battleDamage,
                    policy, cancellationToken, progressCallback, policy.Profile,
                    potionPolicyOverride: SolverPotionPolicy.Disabled,
                    maximumPotionUses: 0);
                PlanAction[] beforeEndTurn = firstTurn[..^1];
                string chosenKey = CombatBeamSolver.TurnEndChoiceKey(chosenEndTurn);
                foreach (PlanAction alternative in choiceBuilder
                             .BuildTurnEndChoiceActionsAfterPrefix(beforeEndTurn)
                             .Where(action => CombatBeamSolver.TurnEndChoiceKey(action) != chosenKey)
                             .Take(2))
                {
                    int remainingMilliseconds = ledger.RemainingRequestMilliseconds;
                    long remainingNodes = ledger.RemainingNodes(policy.Profile);
                    if (remainingMilliseconds <= 25_000 || remainingNodes <= 0)
                        break;
                    SolverSearchProfile choiceProfile = policy.Profile with
                    {
                        MaxExpandedNodes = (int)Math.Min(80_000, remainingNodes),
                        SoftTimeBudgetMilliseconds = Math.Min(20_000, remainingMilliseconds - 2_000),
                    };
                    SolverResult candidate = new CombatBeamSolver(root, displayNames,
                        battleDamage, policy, cancellationToken, progressCallback,
                        choiceProfile, potionPolicyOverride: SolverPotionPolicy.Disabled,
                        maximumPotionUses: 0,
                        fixedPrefixActions: [.. beforeEndTurn, alternative],
                        resetFixedPrefixSchedulingBaseline: true).Solve();
                    bool improved = candidate.ResultScope == SolverResultScope.SearchCompletion
                        && candidate.ProjectedBattleHpLost < selected.ProjectedBattleHpLost
                        && IsBetterPotionPolicyResult(root, policy, candidate, selected);
                    policy.Diagnostics.Info($"[CombatSolver/Test] TURN_END_CHOICE_POSTERIOR " +
                        $"choice={CombatBeamSolver.TurnEndChoiceKey(alternative)} " +
                        $"hp_lost={candidate.ProjectedBattleHpLost} " +
                        $"potions={candidate.PotionCount} selected={improved}");
                    if (improved)
                        selected = candidate;
                }
                PlanAction[] selectedFirstTurn = selected.BestNode.Actions
                    .TakeWhile(action => action.Turn == root.StartTurnNumber)
                    .ToArray();
                if (selectedFirstTurn.LastOrDefault() is
                    { Kind: PlanActionKind.EndTurn, TurnStartChoices: { Count: > 0 } } selectedEndTurn
                    && CombatBeamSolver.TurnEndChoiceKey(selectedEndTurn) != chosenKey)
                {
                    PlanAction[] played = selectedFirstTurn[..^1];
                    for (int index = 0; index + 1 < played.Length; index++)
                    {
                        PlanAction retrieval = played[index];
                        PlanAction followUp = played[index + 1];
                        if (retrieval.Choice is not { Effect: PlanChoiceEffect.MoveToHand } choice
                            || followUp.Kind != PlanActionKind.PlayCard
                            || !choice.Cards.Any(card => card.CardId == followUp.CardId))
                            continue;
                        PlanAction[] shorter = [.. played[..index], .. played[(index + 2)..]];
                        if (ledger.RemainingRequestMillisecondsLong <= 25_000)
                            break;
                        PlanAction? endTurn = choiceBuilder
                            .BuildTurnEndChoiceActionsAfterPrefix(shorter)
                            .FirstOrDefault(action => CombatBeamSolver.TurnEndChoiceKey(action)
                                == CombatBeamSolver.TurnEndChoiceKey(selectedEndTurn));
                        if (endTurn == null)
                            continue;
                        int remainingMilliseconds = ledger.RemainingRequestMilliseconds;
                        long remainingNodes = ledger.RemainingNodes(policy.Profile);
                        if (remainingMilliseconds <= 25_000 || remainingNodes <= 0)
                            break;
                        SolverSearchProfile shorterProfile = policy.Profile with
                        {
                            MaxExpandedNodes = (int)Math.Min(80_000, remainingNodes),
                            SoftTimeBudgetMilliseconds = Math.Min(20_000, remainingMilliseconds - 2_000),
                        };
                        SolverResult candidate = new CombatBeamSolver(root, displayNames,
                            battleDamage, policy, cancellationToken, progressCallback,
                            shorterProfile, potionPolicyOverride: SolverPotionPolicy.Disabled,
                            maximumPotionUses: 0,
                            fixedPrefixActions: [.. shorter, endTurn],
                            resetFixedPrefixSchedulingBaseline: true).Solve();
                        bool improved = candidate.ResultScope == SolverResultScope.SearchCompletion
                            && candidate.ProjectedBattleHpLost < selected.ProjectedBattleHpLost
                            && IsBetterPotionPolicyResult(root, policy, candidate, selected);
                        policy.Diagnostics.Info($"[CombatSolver/Test] TURN_END_CHOICE_SHORTER_OPENING " +
                            $"hp_lost={candidate.ProjectedBattleHpLost} " +
                            $"potions={candidate.PotionCount} selected={improved}");
                        if (improved)
                            selected = candidate;
                        break;
                    }
                }
            }
        }
        return selected;
    }

    private static SolverResult RunEarlierCopyDelayedDamage(SearchPassContext context, SolverResult selected)
    {
        CombatRootSnapshot root = context.Root;
        SolverDisplayNames displayNames = context.DisplayNames;
        BattleDamageSnapshot battleDamage = context.BattleDamage;
        SearchPolicySnapshot policy = context.Policy;
        CancellationToken cancellationToken = context.CancellationToken;
        Action<SolverProgress>? progressCallback = context.ProgressCallback;
        SearchBudgetLedger ledger = context.Budget;
        if (selected.ResultScope == SolverResultScope.SearchCompletion
            && policy.PotionPolicy == SolverPotionPolicy.Smart
            && !policy.PotionStrategy.HasForcedDirectives
            && IsCompleteVictory(selected)
            && selected.ProjectedBattleHpLost > 0
            && selected.BestNode.Actions.Any(action => action is
                { Kind: PlanActionKind.UsePotion, PotionId: "DUPLICATOR" }))
        {
            CombatBeamSolver copyBuilder = new(root, displayNames, battleDamage,
                policy, cancellationToken, progressCallback, policy.Profile);
            foreach (PlanAction[] prefix in copyBuilder
                         .BuildEarlierCopyPotionDelayedDamagePrefixes(selected.BestNode.Actions))
            {
                int remainingMilliseconds = ledger.RemainingRequestMilliseconds;
                long remainingNodes = ledger.RemainingNodes(policy.Profile);
                if (remainingMilliseconds <= 20_000 || remainingNodes <= 0)
                    break;
                SolverSearchProfile copyProfile = policy.Profile with
                {
                    MaxExpandedNodes = (int)Math.Min(80_000, remainingNodes),
                    SoftTimeBudgetMilliseconds = Math.Min(18_000, remainingMilliseconds - 2_000),
                };
                SolverResult candidate = new CombatBeamSolver(root, displayNames,
                    battleDamage, policy, cancellationToken, progressCallback,
                    copyProfile, potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: selected.ExplicitPotionCount,
                    fixedPrefixActions: prefix,
                    resetFixedPrefixSchedulingBaseline: true,
                    minimumPotionUses: selected.ExplicitPotionCount).Solve();
                bool improved = candidate.ResultScope == SolverResultScope.SearchCompletion
                    && candidate.ProjectedBattleHpLost < selected.ProjectedBattleHpLost
                    && IsBetterPotionPolicyResult(root, policy, candidate, selected);
                policy.Diagnostics.Info($"[CombatSolver/Test] EARLIER_COPY_DELAYED_DAMAGE " +
                    $"target={prefix[^1].CardId} " +
                    $"hp_lost={candidate.ProjectedBattleHpLost} " +
                    $"potions={candidate.PotionCount} selected={improved}");
                if (improved)
                    selected = candidate;
            }
        }
        return selected;
    }
}
