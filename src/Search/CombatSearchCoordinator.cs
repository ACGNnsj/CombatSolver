using System.Diagnostics;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Rooms;

namespace CombatSolver;

internal static partial class CombatSearchCoordinator
{
    public static SolverResult Solve(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback)
    {
        Stopwatch requestClock = Stopwatch.StartNew();
        List<PlanAction[]> firstTurnAnchors = [];
        SearchBudgetLedger ledger = new(requestClock, policy);
        SearchRequestWorkTotals requestWorkTotals = ledger.WorkTotals;
        BeamWidthPortfolioTelemetry portfolioTelemetry = new();
        policy = policy with
        {
            RequestWorkTotals = requestWorkTotals,
            PortfolioTelemetry = portfolioTelemetry,
        };
        SearchInteractionState? interaction = policy.Interaction;
        SolverResult? currentCompleteAdoptableResult = null;
        SolverInterimResult? currentDisplayedResult = null;
        SolverProgress? lastProgress = null;
        int currentTurnPreviewVersion = 0;
        int speculativeRouteVersion = 0;
        SolverCurrentTurnPreview? currentTurnPreview = null;
        SolverSpeculativeRoutePreview? speculativeRoutePreview = null;
        SolverRouteAdoptionSeed? currentRouteAdoptionSeed = null;

        bool TryPromoteDisplayedResult(SolverInterimResult candidate)
        {
            if (currentDisplayedResult != null)
            {
                if (candidate == currentDisplayedResult)
                    return true;
                if (!SolverInterimResultOrdering.CanPromoteDisplayedResult(
                        candidate,
                        currentDisplayedResult))
                    return false;
            }
            currentDisplayedResult = candidate;
            return true;
        }

        void PublishAdoptableResult(SolverResult result)
        {
            if (result.OnlyDeathRoutesFound
                || !SolverInterimResultOrdering.IsCompleteVictory(
                    result.BestNode.ActionCount,
                    result.Snapshot.AllEnemiesDead,
                    result.Snapshot.PlayerDead,
                    result.Snapshot.ProjectedPlayerHp))
            {
                return;
            }

            SolverInterimResult summary = BuildInterimResult(root, policy, result);
            bool promoted = TryPromoteDisplayedResult(summary);
            if (!promoted && summary != currentDisplayedResult)
                return;
            currentCompleteAdoptableResult = result;
            currentTurnPreview = SolverCurrentTurnPreview.FromResult(
                result,
                ++currentTurnPreviewVersion);
            speculativeRoutePreview = SolverSpeculativeRoutePreview.FromResult(
                result,
                ++speculativeRouteVersion);
            SolverRouteAdoptionSeed seed = new(
                speculativeRoutePreview.CandidateVersion,
                result.BestNode.Actions,
                () => result);
            currentRouteAdoptionSeed = seed;
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SEARCH_INTERIM_RESULT potions={result.ProjectedBattlePotionCount} " +
                $"projected_battle_hp_lost={result.ProjectedBattleHpLost}");
            if (lastProgress != null && progressCallback != null)
            {
                lastProgress = lastProgress with
                {
                    CurrentBestResult = currentDisplayedResult,
                    CurrentTurnPreview = currentTurnPreview,
                    SpeculativeRoutePreview = speculativeRoutePreview,
                    RouteAdoptionSeed = currentRouteAdoptionSeed,
                };
                progressCallback(lastProgress);
            }
        }

        Action<SolverProgress>? enrichedProgressCallback = progressCallback == null
            ? null
            : progress =>
            {
                lastProgress = progress;
                // Supplemental searches publish their own local previews. Once a global best exists,
                // keep those previews and their adoption seed together unless that local result wins globally.
                bool acceptsRouteUpdate = currentDisplayedResult == null;
                if (progress.CurrentBestResult is { } candidate)
                {
                    acceptsRouteUpdate = TryPromoteDisplayedResult(candidate);
                }
                else if (currentDisplayedResult != null)
                {
                    acceptsRouteUpdate = false;
                }

                if (acceptsRouteUpdate)
                {
                    if (progress.CurrentTurnPreview is { } current)
                    {
                        currentTurnPreview = current;
                        currentTurnPreviewVersion = Math.Max(
                            currentTurnPreviewVersion,
                            current.CandidateVersion);
                    }
                    if (progress.SpeculativeRoutePreview is { } speculative)
                    {
                        speculativeRoutePreview = speculative;
                        currentRouteAdoptionSeed = progress.RouteAdoptionSeed;
                        speculativeRouteVersion = Math.Max(
                            speculativeRouteVersion,
                            speculative.CandidateVersion);
                    }
                }
                progressCallback(progress with
                {
                    CurrentBestResult = currentDisplayedResult,
                    CurrentTurnPreview = currentTurnPreview,
                    SpeculativeRoutePreview = speculativeRoutePreview,
                    RouteAdoptionSeed = currentRouteAdoptionSeed,
                });
            };
        SolverResult RunPostSearch(SolverResult result)
        {
            SolverResult selected = ResolveTakeoverResult(result, interaction) ?? result;
            if (policy.IncludeTurnSetup)
                return selected;
            SearchPassContext postContext = new(root, displayNames, battleDamage,
                policy, policy.Profile, requestClock, ledger, cancellationToken,
                enrichedProgressCallback, interaction == null ? null : PublishAdoptableResult);
            return RunPostSearchPasses(postContext, selected, firstTurnAnchors,
                interaction, () => currentCompleteAdoptableResult);
        }

        try
        {
            SolverResult result = SolveCore(
                root,
                displayNames,
                battleDamage,
                policy,
                ledger,
                cancellationToken,
                enrichedProgressCallback,
                interaction == null ? null : PublishAdoptableResult,
                firstTurnAnchors.Add,
                RunPostSearch);
            SolverResult selected = result;
            if (policy.IncludeTurnSetup)
            {
                PopulateRequestWorkTotals(selected, requestWorkTotals);
                selected.PortfolioTelemetry = portfolioTelemetry;
                return selected;
            }
            PopulateRequestWorkTotals(selected, requestWorkTotals);
            selected.PortfolioTelemetry = portfolioTelemetry;
            selected.ComparisonQuality = BuildInterimResult(root, policy, selected);
            selected.ComparisonRootState = root.ContinuationStamp.StateText;
            return selected;
        }
        catch (OperationCanceledException)
            when (interaction?.CurrentTakeoverRequest?.Kind == SearchTakeoverKind.ApplyCurrentTurn
                  && currentCompleteAdoptableResult != null)
        {
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SEARCH_INTERIM_ADOPTED " +
                $"potions={currentCompleteAdoptableResult.ProjectedBattlePotionCount} " +
                $"projected_battle_hp_lost={currentCompleteAdoptableResult.ProjectedBattleHpLost}");
            PopulateRequestWorkTotals(currentCompleteAdoptableResult, requestWorkTotals);
            currentCompleteAdoptableResult.PortfolioTelemetry = portfolioTelemetry;
            return currentCompleteAdoptableResult;
        }
    }

    private static bool IsAdoptionResult(SolverResult result)
        => result.ResultScope is SolverResultScope.CurrentTurnAdoption
            or SolverResultScope.RouteAdoption
            || SolverInterimResultOrdering.IsCompleteVictory(
                result.BestNode.ActionCount,
                result.Snapshot.AllEnemiesDead,
                result.Snapshot.PlayerDead,
                result.Snapshot.ProjectedPlayerHp);

    private static SolverResult? ResolveTakeoverResult(
        SolverResult result,
        SearchInteractionState? interaction)
    {
        SearchTakeoverRequest? request = interaction?.CurrentTakeoverRequest;
        if (request == null)
            return null;
        if (result.ResultScope is SolverResultScope.CurrentTurnAdoption
            or SolverResultScope.RouteAdoption)
        {
            return result;
        }
        if (request.Kind == SearchTakeoverKind.AdoptRoute)
            return request.RouteAdoptionSeed?.Materialize();
        return IsAdoptionResult(result) ? result : null;
    }

    private static SolverResult SolveCore(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        SearchBudgetLedger ledger,
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback,
        Action<SolverResult>? interimResultCallback,
        Action<PlanAction[]> firstTurnAnchorObserver,
        Func<SolverResult, SolverResult> postSearch)
    {
        Stopwatch requestClock = Stopwatch.StartNew();
        bool forcedSmartGradient = policy.PotionPolicy == SolverPotionPolicy.Smart
            && policy.PotionStrategy.HasForcedDirectives;
        SearchPolicySnapshot forcedBaselinePolicy = forcedSmartGradient
            ? policy with { PotionStrategy = policy.PotionStrategy.ForForcedBaseline() }
            : policy;
        SolverPotionPolicy? initialPotionPolicyOverride = policy.PotionPolicy == SolverPotionPolicy.Smart
            && !policy.PotionStrategy.HasForcedDirectives
                ? SolverPotionPolicy.Disabled
                : null;
        // The progress bar represents the whole request. Individual Beam, novelty,
        // refinement and potion-audit searches all consume this same time budget.
        SolverSearchProfile profile = policy.Profile;
        if (policy.BudgetOverrideMilliseconds is { } deepBudget)
            profile = profile with { SoftTimeBudgetMilliseconds = deepBudget };
        if (progressCallback != null)
        {
            long completedSearches = 0;
            long completedElapsed = 0;
            int lastExpanded = 0;
            long lastElapsed = 0;
            Action<SolverProgress> publishProgress = progressCallback;
            progressCallback = progress =>
            {
                if (progress.ExpandedNodes < lastExpanded
                    || progress.ElapsedMilliseconds < lastElapsed)
                {
                    completedSearches += lastExpanded;
                    completedElapsed += lastElapsed;
                }
                lastExpanded = progress.ExpandedNodes;
                lastElapsed = progress.ElapsedMilliseconds;
                publishProgress(progress with
                {
                    ReviewedWorldlines = completedSearches + progress.ExpandedNodes,
                    ElapsedMilliseconds = completedElapsed + progress.ElapsedMilliseconds,
                    RequestBudgetMilliseconds = profile.SoftTimeBudgetMilliseconds,
                });
            };
        }
        SmartLayerMemoryForecast memoryForecast = new();
        // One search profile drives primary search and all supplemental audits.
        if (root.IsActEndingBoss && profile.BeamWidth < 45)
        {
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] ACT_ENDING_BOSS_SEARCH_OVERRIDE " +
                $"beam={profile.BeamWidth}->45 reason=preserve_survival_routes");
            profile = profile with { BeamWidth = 45 };
        }
        // 一轮完整的深化搜索：主搜索（Smart 时先按无主动用药跑）＋补充审计。抬节点上限重搜时
        // 原样再走一遍，所以抽成一个本地函数；每一轮自带一只秒表，补充审计那边算剩余预算靠它。
        SearchPassResult RunSearchPass(SearchPassContext passContext)
        {
            SolverSearchProfile passProfile = passContext.Profile;
            Stopwatch passClock = passContext.Clock;
            FrontierContinuationScheduler continuationScheduler = new(passContext);
            SearchPassResult CapturePassResult(
                SolverResult selected,
                SolverResult? takeover,
                bool settled)
                => new(selected, takeover, settled,
                    selected.ResultScope == SolverResultScope.SearchCompletion
                        ? RouteQuality.FromInterim(BuildInterimResult(root, policy, selected))
                        : null,
                    passContext.Budget.WorkTotals.Snapshot(),
                    selected.ResultScope,
                    selected.BoundaryReason);
            long passAllocatedAtStart = GC.GetTotalAllocatedBytes(precise: false);
            long passTransitionsAtStart = passContext.Budget.WorkTotals.Snapshot().TransitionCount;
            SearchPolicySnapshot passPolicy = forcedBaselinePolicy;
            SearchPolicySnapshot beamPolicy = passPolicy.NoveltySearch == null
                ? passPolicy : passPolicy with { NoveltySearch = null };
            SolverResult SolveMember(SolverSearchProfile memberProfile, bool refinement)
            {
                Action<SolverProgress>? memberProgressCallback = refinement && progressCallback != null
                    ? progress => progressCallback(progress with { Phase = "正在精炼路线" })
                    : progressCallback;
                SolverResult memberResult = new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    beamPolicy,
                    cancellationToken,
                    memberProgressCallback,
                    memberProfile,
                    potionPolicyOverride: initialPotionPolicyOverride).Solve();
                PlanAction[] firstTurn = memberResult.BestNode.Actions
                    .TakeWhile(action => action.Turn == root.StartTurnNumber)
                    .ToArray();
                if (firstTurn.LastOrDefault()?.Kind == PlanActionKind.EndTurn)
                    firstTurnAnchorObserver(firstTurn);
                return memberResult;
            }
            // 基线成员一跑完就按今天的方式把完整结果发布给覆盖层（覆盖层的中途路线走
            // SolverProgress，见 RunBeamWidthPortfolioPass 的注释）；精炼成员只有更优时才会
            // 在本轮末尾再发布一次，所以同一份结果不会发布两遍。
            SolverResult? publishedBaseline = null;
            Action<SolverResult>? publishBaseline =
                (policy.UseBeamWidthPortfolio
                    || root.PlayerCardIds.Any(PowerCardValuationModels.Registry.ContainsCardId))
                && interimResultCallback != null
                    ? baseline =>
                    {
                        publishedBaseline = baseline;
                        interimResultCallback(baseline);
                    }
                    : null;
            SolverResult RunBaseline(SolverSearchProfile baselineProfile)
                => RunBeamWidthPortfolioPass(passContext with
                    {
                        Policy = beamPolicy,
                        Profile = baselineProfile,
                        Clock = ReferenceEquals(baselineProfile, passProfile)
                            ? passClock : Stopwatch.StartNew(),
                    }, SolveMember, publishBaseline);
            SolverResult RunPrimary()
                => policy.UseNoveltyPortfolio
                    ? RunNoveltyPortfolioPass(passContext with { Policy = passPolicy },
                        initialPotionPolicyOverride, RunBaseline)
                    : RunBaseline(passProfile);
            bool hasForcedBaseline = forcedSmartGradient;
            SolverResult passResult;
            try
            {
                passResult = RunPrimary();
            }
            catch (PotionPolicyUnsatisfiedException) when (forcedSmartGradient)
            {
                // The forced-only layer has no usable route; optional potions can still rescue the fight.
                hasForcedBaseline = false;
                passPolicy = policy;
                beamPolicy = policy.NoveltySearch == null
                    ? policy : policy with { NoveltySearch = null };
                passResult = RunPrimary();
            }
            // Opening posteriors require a resolved hand; turn setup still owns its native choice.
            if (!policy.IncludeTurnSetup)
            {
            if (passResult.ResultScope == SolverResultScope.SearchCompletion)
            {
                passResult = RunOpeningPowerRoutePortfolio(
                    passContext with { Policy = beamPolicy },
                    initialPotionPolicyOverride,
                    passResult);
            }
            if (passResult.ResultScope == SolverResultScope.SearchCompletion
                && IsCompleteVictory(passResult)
                && passResult.ExplicitPotionCount == 0
                && passResult.ProjectedBattleHpLost >= SolverWeights.PotionMinimumHpSaved
                && initialPotionPolicyOverride == SolverPotionPolicy.Disabled)
            {
                PlanAction[] opening = passResult.BestNode.Actions
                    .TakeWhile(action => action.Turn == root.StartTurnNumber).ToArray();
                int generatedIndex = Array.FindIndex(opening, action =>
                    action is { Kind: PlanActionKind.PlayCard, CardId: not null }
                    && !root.PlayerCardIds.Contains(action.CardId));
                int discardIndex = generatedIndex < 1 ? -1 : Array.FindIndex(
                    opening, generatedIndex + 1, action => action.Choice?.Effect is
                        PlanChoiceEffect.Discard or PlanChoiceEffect.DiscardAndDraw);
                if (discardIndex > generatedIndex
                    && opening[generatedIndex - 1].Kind == PlanActionKind.PlayCard)
                {
                    PlanAction[] prefix = [.. opening.Take(generatedIndex - 1), opening[discardIndex]];
                    SearchBudgetWindow reorderedWindow = passContext.SliceWindow;
                    CombatBeamSolver builder = new(root, displayNames, battleDamage,
                        beamPolicy, cancellationToken, progressCallback, passProfile,
                        potionPolicyOverride: SolverPotionPolicy.Disabled, maximumPotionUses: 0);
                    if (reorderedWindow.CanStart(5_000)
                        && builder.CanReplayOpeningPrefix(prefix))
                    {
                        SolverSearchProfile reorderedProfile = reorderedWindow.Limit(passProfile,
                            maximumNodes: 100_000, maximumMilliseconds: 30_000,
                            reserveMilliseconds: 2_000);
                        SolverResult candidate = continuationScheduler.Dispatch(
                            new ContinuationSearchRequest(passContext,
                                ContinuationPurpose.EarlyDiscardBeforeGeneration,
                                prefix, reorderedProfile, SolverPotionPolicy.Disabled, 0, null)
                            { PolicyOverride = beamPolicy });
                        bool improved = candidate.ResultScope == SolverResultScope.SearchCompletion
                            && IsBetterPotionPolicyResult(root, policy, candidate, passResult);
                        if (improved)
                            passResult = candidate;
                        policy.Diagnostics.Info($"[CombatSolver/Test] EARLY_DISCARD_BEFORE_GENERATION " +
                            $"hp_lost={candidate.ProjectedBattleHpLost} selected={improved}");
                    }
                }
            }
            if (passResult.ResultScope == SolverResultScope.SearchCompletion
                && root.Enemies.Count > 1
                && passResult.ProjectedBattleHpLost >= SolverWeights.PotionMinimumHpSaved
                && initialPotionPolicyOverride == SolverPotionPolicy.Disabled)
            {
                PlanAction[] opening = passResult.BestNode.Actions
                    .TakeWhile(action => action.Turn == root.StartTurnNumber).ToArray();
                CombatBeamSolver targetBuilder = new(root, displayNames, battleDamage,
                    beamPolicy, cancellationToken, progressCallback, passProfile,
                    potionPolicyOverride: SolverPotionPolicy.Disabled, maximumPotionUses: 0);
                foreach (PlanAction[] prefix in targetBuilder
                             .BuildOpeningLeadingTargetPrefixes(opening)
                             .DistinctBy(PowerPrefixKey).Take(4))
                {
                    SearchBudgetWindow targetWindow = passContext.SliceWindow;
                    if (!targetWindow.CanStart(5_000))
                        break;
                    SolverSearchProfile targetProfile = targetWindow.Limit(passProfile,
                        maximumNodes: 40_000, maximumMilliseconds: 15_000,
                        reserveMilliseconds: 2_000) with
                    {
                        AggressivePowerCommitment = true,
                    };
                    SolverResult candidate = continuationScheduler.Dispatch(
                        new ContinuationSearchRequest(passContext,
                            ContinuationPurpose.OpeningTargetVariant,
                            prefix, targetProfile, SolverPotionPolicy.Disabled, 0, null)
                        { PolicyOverride = beamPolicy });
                    if (candidate.ResultScope == SolverResultScope.SearchCompletion
                        && IsBetterPotionPolicyResult(root, policy, candidate, passResult))
                        passResult = candidate;
                    policy.Diagnostics.Info($"[CombatSolver/Test] OPENING_TARGET_VARIANT " +
                        $"prefix={string.Join('+', prefix.Select(action => $"{action.CardId}@{action.TargetCombatId}"))} " +
                        $"hp_lost={candidate.ProjectedBattleHpLost}");
                    if (HasReachedAcceptableBattleHpLoss(policy, passResult))
                        break;
                    foreach (PlanAction power in targetBuilder.BuildPowerActionsAfterPrefix(prefix)
                                 .Where(action => PowerCardValuationModels.Registry.ContainsCardId(action.CardId!))
                                 .Take(1))
                    {
                        SearchBudgetWindow powerWindow = passContext.SliceWindow;
                        if (!powerWindow.CanStart(5_000))
                            break;
                        SolverSearchProfile powerProfile = powerWindow.Limit(targetProfile,
                            maximumNodes: 40_000, maximumMilliseconds: 15_000,
                            reserveMilliseconds: 2_000) with
                        {
                            BaseScoreOnly = true,
                            AggressivePowerCommitment = false,
                        };
                        SolverResult powered = continuationScheduler.Dispatch(
                            new ContinuationSearchRequest(passContext,
                                ContinuationPurpose.OpeningTargetPowerVariant,
                                [.. prefix, power], powerProfile,
                                SolverPotionPolicy.Disabled, 0, null)
                            { PolicyOverride = beamPolicy });
                        if (powered.ResultScope == SolverResultScope.SearchCompletion
                            && IsBetterPotionPolicyResult(root, policy, powered, passResult))
                            passResult = powered;
                        policy.Diagnostics.Info($"[CombatSolver/Test] OPENING_TARGET_POWER_VARIANT " +
                            $"power={power.CardId} hp_lost={powered.ProjectedBattleHpLost}");
                        foreach (PlanAction defensive in targetBuilder
                                     .BuildOpeningDefensiveFollowUps([.. prefix, power]))
                        {
                            SearchBudgetWindow defensiveWindow = passContext.SliceWindow;
                            if (!defensiveWindow.CanStart(5_000))
                                break;
                            SolverSearchProfile defensiveProfile = defensiveWindow.Limit(
                                powerProfile, maximumNodes: 40_000, maximumMilliseconds: 15_000,
                                reserveMilliseconds: 2_000);
                            SolverResult defended = continuationScheduler.Dispatch(
                                new ContinuationSearchRequest(passContext,
                                    ContinuationPurpose.OpeningTargetPowerDefensiveVariant,
                                    [.. prefix, power, defensive], defensiveProfile,
                                    SolverPotionPolicy.Disabled, 0, null)
                                { PolicyOverride = beamPolicy });
                            if (defended.ResultScope == SolverResultScope.SearchCompletion
                                && IsBetterPotionPolicyResult(root, policy, defended, passResult))
                                passResult = defended;
                            policy.Diagnostics.Info($"[CombatSolver/Test] OPENING_TARGET_POWER_DEFENSIVE_VARIANT " +
                                $"power={power.CardId} defensive={defensive.CardId} " +
                                $"hp_lost={defended.ProjectedBattleHpLost}");
                        }
                    }
                }
            }
            if (passResult.ResultScope == SolverResultScope.SearchCompletion
                && IsCompleteVictory(passResult)
                && passResult.ExplicitPotionCount == 0
                && passResult.ProjectedBattleHpLost >= SolverWeights.PotionMinimumHpSaved
                && root.PlayerCardIds.Any(PowerCardValuationModels.Registry.ContainsCardId)
                && initialPotionPolicyOverride == SolverPotionPolicy.Disabled)
            {
                PlanAction endTurn = new(PlanActionKind.EndTurn, root.StartTurnNumber);
                CombatBeamSolver deferredBuilder = new(root, displayNames, battleDamage,
                    beamPolicy, cancellationToken, progressCallback, passProfile,
                    potionPolicyOverride: SolverPotionPolicy.Disabled, maximumPotionUses: 0);
                if (deferredBuilder.CanReplayOpeningPrefix([endTurn]))
                {
                    foreach (PlanAction power in deferredBuilder
                                 .BuildPowerActionsAfterPrefix([endTurn])
                                 .Where(action => PowerCardValuationModels.Registry.ContainsCardId(action.CardId!))
                                 .Take(2))
                    {
                        SearchBudgetWindow deferredWindow = passContext.SliceWindow;
                        if (!deferredWindow.CanStart(5_000))
                            break;
                        SolverSearchProfile deferredProfile = deferredWindow.Limit(passProfile,
                            maximumNodes: 50_000, maximumMilliseconds: 20_000,
                            reserveMilliseconds: 2_000);
                        SolverResult deferred = continuationScheduler.Dispatch(
                            new ContinuationSearchRequest(passContext,
                                ContinuationPurpose.DeferredOpeningPower,
                                [endTurn, power], deferredProfile,
                                SolverPotionPolicy.Disabled, 0, null)
                            { PolicyOverride = beamPolicy });
                        if (deferred.ResultScope == SolverResultScope.SearchCompletion
                            && IsBetterPotionPolicyResult(root, policy, deferred, passResult))
                            passResult = deferred;
                        policy.Diagnostics.Info($"[CombatSolver/Test] DEFERRED_OPENING_POWER " +
                            $"power={power.CardId} hp_lost={deferred.ProjectedBattleHpLost}");
                    }
                }
            }
            if (passResult.ResultScope == SolverResultScope.SearchCompletion
                && IsCompleteVictory(passResult)
                && passResult.ExplicitPotionCount == 0
                && passResult.ProjectedBattleHpLost >= SolverWeights.PotionMinimumHpSaved
                && initialPotionPolicyOverride == SolverPotionPolicy.Disabled)
            {
                CombatBeamSolver openingBuilder = new(root, displayNames, battleDamage,
                    beamPolicy, cancellationToken, progressCallback, passProfile,
                    potionPolicyOverride: SolverPotionPolicy.Disabled, maximumPotionUses: 0);
                IReadOnlyList<PlanAction> freeAttacks = openingBuilder.BuildOpeningFreeOffensiveActions();
                foreach (PlanAction attack in freeAttacks)
                {
                    IReadOnlyList<PlanAction> setups = openingBuilder
                        .BuildOpeningHandCycleActionsAfterPrefix([attack]);
                    foreach (PlanAction setup in setups.Take(2))
                    {
                        PlanAction[] prefix = [attack, setup];
                        SearchBudgetWindow openingWindow = passContext.SliceWindow;
                        if (!openingWindow.CanStart(5_000))
                            break;
                        SolverSearchProfile openingProfile = openingWindow.Limit(passProfile,
                            maximumNodes: 70_000, maximumMilliseconds: 20_000,
                            reserveMilliseconds: 2_000) with
                        {
                            BaseScoreOnly = true,
                        };
                        SolverResult candidate = continuationScheduler.Dispatch(
                            new ContinuationSearchRequest(passContext,
                                ContinuationPurpose.FreeAttackHandSetup,
                                prefix, openingProfile, SolverPotionPolicy.Disabled, 0, null)
                            { PolicyOverride = beamPolicy });
                        if (candidate.ResultScope == SolverResultScope.SearchCompletion
                            && IsBetterPotionPolicyResult(root, policy, candidate, passResult))
                            passResult = candidate;
                        policy.Diagnostics.Info($"[CombatSolver/Test] FREE_ATTACK_HAND_SETUP " +
                            $"attack={attack.CardId} setup={setup.CardId} " +
                            $"hp_lost={candidate.ProjectedBattleHpLost}");
                    }
                }
            }
            }
            NoveltyPortfolioTelemetry? noveltyPass = passResult.NoveltyPortfolio;
            ObserveSmartLayerMemory(
                passContext, memoryForecast, passAllocatedAtStart, passTransitionsAtStart,
                passResult, passProfile,
                completedPotionCount: hasForcedBaseline ? policy.PotionStrategy.ForcedDirectiveCount : 0);
            if (policy.MeasurePhasePerformance)
                policy.Diagnostics.Info(SolverDiagnostics.DescribeSearchPhasePerformance(passResult));
            passResult.SingleSessionSearch = true;
            PopulateSingleSessionTotals(passResult);
            if (!ReferenceEquals(passResult, publishedBaseline))
                interimResultCallback?.Invoke(passResult);
            if (ResolveTakeoverResult(passResult, policy.Interaction) is { } passTakeover)
                return CapturePassResult(passResult, passTakeover, false);
            if (policy.IncludeTurnSetup)
                return CapturePassResult(passResult, null, false);
            SearchPassContext auditContext = passContext;
            if (passResult.DeterministicBlockPotionInserted)
            {
                SearchPolicySnapshot potionFreePolicy = beamPolicy with
                {
                    PotionPolicy = SolverPotionPolicy.Disabled,
                    PotionStrategy = new PotionStrategySnapshot(SolverPotionPolicy.Disabled, []),
                };
                SolverResult potionFree = new CombatBeamSolver(root, displayNames,
                    battleDamage, potionFreePolicy, cancellationToken, progressCallback,
                    passProfile, potionPolicyOverride: SolverPotionPolicy.Disabled).Solve();
                if (potionFree.ResultScope != SolverResultScope.SearchCompletion)
                    return CapturePassResult(potionFree, null, false);
                SolverResult audited = RunSupplementalAudits(auditContext,
                    potionFree, memoryForecast);
                if (audited.ResultScope != SolverResultScope.SearchCompletion)
                    return CapturePassResult(audited, null, false);
                if (IsBetterSmartPotionAuditResult(root, policy, audited, passResult))
                    passResult = audited;
                return CapturePassResult(passResult, null, false);
            }
            if (!policy.PotionStrategy.HasForcedDirectives || hasForcedBaseline)
            {
                if (!hasForcedBaseline && HasReachedAcceptableBattleHpLoss(policy, passResult))
                    return CapturePassResult(passResult, null, true);
                passResult = RunSupplementalAudits(
                    policy.NoveltySearch == null
                        ? auditContext
                        : auditContext with { Policy = policy with { NoveltySearch = null } },
                    passResult,
                    memoryForecast);
                // The final potion audit may return another result object. Keep the
                // primary-pass observations alongside the request's final outcome.
                passResult.NoveltyPortfolio = noveltyPass;
            }
            return CapturePassResult(passResult, null, false);
        }

        SearchPassContext requestContext = new(root, displayNames, battleDamage,
            policy, profile, requestClock, ledger, cancellationToken,
            progressCallback, interimResultCallback);
        return new SearchRequestPipeline(requestContext, RunSearchPass, postSearch).Run();
    }

    private static bool IsProvenZeroDamageRoute(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult result)
        => !policy.EffectiveHasGrowthTargets
            && IsCompleteVictory(result)
            && result.ExplicitPotionCount == 0
            && result.FutureSoldHp == 0
            && result.ProjectedBattleHpLost - result.BattleHpLostSoFar == 0
            && result.Snapshot.PlayerMaxHp >= root.InitialPlayerMaxHp
            && result.Snapshot.PlayerHp >= result.Snapshot.PlayerMaxHp;

    private static SolverResult RunSupplementalAudits(
        SearchPassContext context,
        SolverResult primary,
        SmartLayerMemoryForecast memoryForecast)
    {
        CombatRootSnapshot root = context.Root;
        SearchPolicySnapshot policy = context.Policy;
        CancellationToken cancellationToken = context.CancellationToken;
        SolverSearchProfile profile = context.Profile;
        Stopwatch requestClock = context.Clock;
        long remainingMilliseconds = context.RemainingMilliseconds;
        if (remainingMilliseconds <= 0)
        {
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SUPPLEMENTAL_AUDIT_BUDGET exhausted=true " +
                $"elapsed_ms={requestClock.ElapsedMilliseconds} " +
                $"budget_ms={profile.SoftTimeBudgetMilliseconds}");
            return primary;
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(remainingMilliseconds));
        SearchPassContext auditContext = context with { CancellationToken = deadline.Token };
        SolverResult selected = primary;
        try
        {
            if (!policy.PotionStrategy.HasForcedDirectives)
                selected = AuditRequiredPotionUse(auditContext, selected);
            if (ResolveTakeoverResult(selected, policy.Interaction) is { } requiredTakeoverResult)
                return requiredTakeoverResult;
            if (!policy.PotionStrategy.HasForcedDirectives
                && HasReachedAcceptableBattleHpLoss(policy, selected))
                return selected;
            selected = AuditSmartPotionUse(
                auditContext, cancellationToken, selected, memoryForecast);
            if (root.PlayerCardIds.Contains("NIGHTMARE")
                && !IsProvenZeroDamageRoute(root, policy, selected))
            {
                selected = RunOpeningNightmarePortfolio(
                    auditContext, selected);
            }
            if (selected.BestNode.Actions.FirstOrDefault() is
                    { Kind: PlanActionKind.UsePotion }
                && root.PlayerCardIds.Contains("WHITE_NOISE"))
            {
                selected = RunOpeningPowerRoutePortfolio(
                    auditContext,
                    potionPolicyOverride: null,
                    selected,
                    generatedAfterOpeningPotionsOnly: true);
            }
            if (HasReachedAcceptableBattleHpLoss(policy, selected))
                return selected;
            if (policy.PotionPolicy != SolverPotionPolicy.Smart)
            {
                selected = AuditOpeningPowerUse(auditContext, selected);
                if (HasReachedAcceptableBattleHpLoss(policy, selected))
                    return selected;
            }
        }
        catch (OperationCanceledException)
            when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SUPPLEMENTAL_AUDIT_BUDGET exhausted=true " +
                $"elapsed_ms={requestClock.ElapsedMilliseconds} " +
                $"budget_ms={profile.SoftTimeBudgetMilliseconds} " +
                $"selected_potions={selected.PotionCount}");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return selected;
    }

    private static SolverResult AuditOpeningPowerUse(
        SearchPassContext context,
        SolverResult primary)
    {
        CombatRootSnapshot root = context.Root;
        SolverDisplayNames displayNames = context.DisplayNames;
        BattleDamageSnapshot battleDamage = context.BattleDamage;
        SearchPolicySnapshot policy = context.Policy;
        CancellationToken cancellationToken = context.CancellationToken;
        Action<SolverProgress>? progressCallback = context.ProgressCallback;
        SolverSearchProfile profile = context.Profile;
        int primaryDeficit = StrategicHpDeficit(root, policy, primary);
        int maximumSmartPotionUses = policy.PotionPolicy == SolverPotionPolicy.Smart
            ? MaximumSmartPotionUses(root, policy, potionFreeWon: true, primaryDeficit)
            : Math.Max(1, primary.PotionCount);
        if (HasReachedProvablePrimaryQualityLowerBound(root, policy, primary)
            || policy.PotionPolicy == SolverPotionPolicy.RequireAtLeastOne
                && battleDamage.PotionsUsedSoFar == 0)
            return primary;

        IReadOnlyList<PlanAction> openingPotions = policy.PotionPolicy == SolverPotionPolicy.Disabled
            || maximumSmartPotionUses == 0
            ? []
            : new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: maximumSmartPotionUses)
                .BuildOpeningPotionActions();
        IReadOnlyList<PlanAction> generatedCardPotions = openingPotions.Count == 0
            ? []
            : new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: maximumSmartPotionUses)
                .SelectGeneratedCardPotionActions(openingPotions);
        IReadOnlyList<PlanAction> openingResources = new CombatBeamSolver(
                root,
                displayNames,
                battleDamage,
                policy,
                cancellationToken,
                progressCallback,
                profile)
            .BuildOpeningResourceActions();
        List<(PlanAction Potion, PlanAction Power)> potionPowerPairs = [];
        foreach (PlanAction openingPotion in openingPotions)
        {
            IReadOnlyList<PlanAction> powers = new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: maximumSmartPotionUses)
                .BuildPowerActionsAfterPrefix([openingPotion]);
            foreach (PlanAction power in powers)
            {
                potionPowerPairs.Add((openingPotion, power));
                if (potionPowerPairs.Count == 4)
                    break;
            }
            if (potionPowerPairs.Count == 4)
                break;
        }
        if (potionPowerPairs.Count == 0
            && generatedCardPotions.Count == 0
            && openingResources.Count == 0)
            return primary;

        List<SolverResult> searches = [primary];
        SolverResult selected = primary;
        FrontierContinuationScheduler continuationScheduler = new(context);
        foreach (PlanAction openingResource in openingResources)
        {
            PlanAction? defensiveFollowUp = new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile)
                .BuildOpeningDefensiveFollowUp([openingResource]);
            if (defensiveFollowUp == null)
                continue;

            SolverResult resourceDefensePosterior = continuationScheduler.Dispatch(
                new ContinuationSearchRequest(context, ContinuationPurpose.OpeningResourceDefense,
                    [openingResource, defensiveFollowUp], profile, null, null, null)
                { ResetFixedPrefixSchedulingBaseline = false });
            if (resourceDefensePosterior.ResultScope != SolverResultScope.SearchCompletion)
                return resourceDefensePosterior;

            resourceDefensePosterior.SingleSessionSearch = true;
            PopulateSingleSessionTotals(resourceDefensePosterior);
            searches.Add(resourceDefensePosterior);
            if (HasReachedAcceptableBattleHpLoss(policy, resourceDefensePosterior))
            {
                MergeAuditTotals(resourceDefensePosterior, searches.ToArray());
                return resourceDefensePosterior;
            }

            if (IsBetterCompletedResult(root, policy, resourceDefensePosterior, selected))
                selected = resourceDefensePosterior;
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] OPENING_RESOURCE_DEFENSE_POSTERIOR " +
                $"cards={openingResource.CardId}+{defensiveFollowUp.CardId} " +
                $"won={resourceDefensePosterior.Snapshot.AllEnemiesDead && !resourceDefensePosterior.Snapshot.PlayerDead} " +
                $"hp_deficit={StrategicHpDeficit(root, policy, resourceDefensePosterior)} " +
                $"selected={ReferenceEquals(selected, resourceDefensePosterior)}");
        }

        foreach (PlanAction openingPotion in generatedCardPotions)
        {
            SolverResult? resourcePosterior = continuationScheduler.DispatchOptional(
                new ContinuationSearchRequest(context, ContinuationPurpose.PotionResourcePosterior,
                    [openingPotion], profile, SolverPotionPolicy.RequireAtLeastOne, 1, null)
                { ResetFixedPrefixSchedulingBaseline = false },
                $"POTION_RESOURCE_POSTERIOR potion={openingPotion.PotionId}");
            if (resourcePosterior == null)
                continue;
            if (resourcePosterior.ResultScope != SolverResultScope.SearchCompletion)
                return resourcePosterior;

            resourcePosterior.SingleSessionSearch = true;
            PopulateSingleSessionTotals(resourcePosterior);
            searches.Add(resourcePosterior);
            if (HasReachedAcceptableBattleHpLoss(policy, resourcePosterior))
            {
                MergeAuditTotals(resourcePosterior, searches.ToArray());
                return resourcePosterior;
            }

            bool resourceWon = resourcePosterior.Snapshot.AllEnemiesDead
                && !resourcePosterior.Snapshot.PlayerDead
                && resourcePosterior.Snapshot.ProjectedPlayerHp > 0;
            int resourceDeficit = StrategicHpDeficit(root, policy, resourcePosterior);
            if (IsBetterCompletedResult(root, policy, resourcePosterior, selected))
            {
                selected = resourcePosterior;
            }
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] POTION_RESOURCE_POSTERIOR " +
                $"potion={openingPotion.PotionId} card={openingPotion.Choice!.Cards[0].CardId} " +
                $"won={resourceWon} hp_deficit={resourceDeficit} " +
                $"selected={ReferenceEquals(selected, resourcePosterior)}");
        }

        if (HasReachedProvablePrimaryQualityLowerBound(root, policy, selected)
            && selected.PotionCount <= 1)
        {
            MergeAuditTotals(selected, searches.ToArray());
            return selected;
        }

        foreach ((PlanAction openingPotion, PlanAction postPotionPower) in potionPowerPairs)
        {
            PlanAction[] jointPrefix = [openingPotion, postPotionPower];
            SolverResult? jointPosterior = continuationScheduler.DispatchOptional(
                new ContinuationSearchRequest(context, ContinuationPurpose.PotionPowerPosterior,
                    jointPrefix, profile, SolverPotionPolicy.RequireAtLeastOne,
                    maximumSmartPotionUses, null)
                { ResetFixedPrefixSchedulingBaseline = false },
                $"POTION_POWER_POSTERIOR potion={openingPotion.PotionId} power={postPotionPower.CardId}");
            if (jointPosterior == null)
                continue;
            if (jointPosterior.ResultScope != SolverResultScope.SearchCompletion)
                return jointPosterior;

            jointPosterior.SingleSessionSearch = true;
            PopulateSingleSessionTotals(jointPosterior);
            searches.Add(jointPosterior);
            if (HasReachedAcceptableBattleHpLoss(policy, jointPosterior))
            {
                MergeAuditTotals(jointPosterior, searches.ToArray());
                return jointPosterior;
            }

            bool jointWon = jointPosterior.Snapshot.AllEnemiesDead
                && !jointPosterior.Snapshot.PlayerDead
                && jointPosterior.Snapshot.ProjectedPlayerHp > 0;
            int jointDeficit = StrategicHpDeficit(root, policy, jointPosterior);
            int comparisonDeficit = StrategicHpDeficit(root, policy, selected);
            if (IsBetterCompletedResult(root, policy, jointPosterior, selected))
            {
                selected = jointPosterior;
            }
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] POTION_POWER_POSTERIOR " +
                $"potion={openingPotion.PotionId} power={postPotionPower.CardId} " +
                $"won={jointWon} hp_deficit={jointDeficit} " +
                $"selected={ReferenceEquals(selected, jointPosterior)}");

            if (!jointWon
                || HasReachedProvablePrimaryQualityLowerBound(root, policy, jointPosterior)
                || jointDeficit > comparisonDeficit + 1)
            {
                continue;
            }

            PlanAction? defensiveFollowUp = new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: maximumSmartPotionUses)
                .BuildOpeningDefensiveFollowUp(jointPrefix);
            if (defensiveFollowUp == null)
                continue;

            SolverResult? defensivePosterior = continuationScheduler.DispatchOptional(
                new ContinuationSearchRequest(context,
                    ContinuationPurpose.PotionPowerDefensivePosterior,
                    [openingPotion, postPotionPower, defensiveFollowUp], profile,
                    SolverPotionPolicy.RequireAtLeastOne, maximumSmartPotionUses, null)
                { ResetFixedPrefixSchedulingBaseline = false },
                $"POTION_POWER_DEFENSIVE_POSTERIOR potion={openingPotion.PotionId} " +
                $"power={postPotionPower.CardId} follow_up={defensiveFollowUp.CardId}");
            if (defensivePosterior == null)
                continue;
            if (defensivePosterior.ResultScope != SolverResultScope.SearchCompletion)
                return defensivePosterior;

            defensivePosterior.SingleSessionSearch = true;
            PopulateSingleSessionTotals(defensivePosterior);
            searches.Add(defensivePosterior);
            if (HasReachedAcceptableBattleHpLoss(policy, defensivePosterior))
            {
                MergeAuditTotals(defensivePosterior, searches.ToArray());
                return defensivePosterior;
            }

            bool defensiveWon = defensivePosterior.Snapshot.AllEnemiesDead
                && !defensivePosterior.Snapshot.PlayerDead
                && defensivePosterior.Snapshot.ProjectedPlayerHp > 0;
            int defensiveDeficit = StrategicHpDeficit(root, policy, defensivePosterior);
            if (IsBetterCompletedResult(root, policy, defensivePosterior, selected))
            {
                selected = defensivePosterior;
            }
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] POTION_POWER_DEFENSIVE_POSTERIOR " +
                $"potion={openingPotion.PotionId} power={postPotionPower.CardId} " +
                $"follow_up={defensiveFollowUp.CardId} won={defensiveWon} " +
                $"hp_deficit={defensiveDeficit} selected={ReferenceEquals(selected, defensivePosterior)}");

            if (HasReachedProvablePrimaryQualityLowerBound(root, policy, defensivePosterior))
                break;
        }

        MergeAuditTotals(selected, searches.ToArray());
        return selected;
    }

    private static SolverResult AuditRequiredPotionUse(
        SearchPassContext context,
        SolverResult primary)
    {
        CombatRootSnapshot root = context.Root;
        SolverDisplayNames displayNames = context.DisplayNames;
        BattleDamageSnapshot battleDamage = context.BattleDamage;
        SearchPolicySnapshot policy = context.Policy;
        CancellationToken cancellationToken = context.CancellationToken;
        Action<SolverProgress>? progressCallback = context.ProgressCallback;
        SolverSearchProfile profile = context.Profile;
        if (policy.PotionPolicy != SolverPotionPolicy.RequireAtLeastOne
            || battleDamage.PotionsUsedSoFar > 0
            || primary.PotionCount <= 1)
        {
            return primary;
        }

        policy.Diagnostics.Info(
            $"[CombatSolver/Test] REQUIRED_POTION_AUDIT start potion_count={primary.PotionCount} " +
            $"reported_saved={primary.PotionHpSaved} required={primary.PotionHpRequired}");
        SolverResult potionFree = new CombatBeamSolver(
            root,
            displayNames,
            battleDamage,
            policy,
            cancellationToken,
            progressCallback,
            profile,
            SolverPotionPolicy.Disabled).Solve();
        if (potionFree.ResultScope != SolverResultScope.SearchCompletion)
            return potionFree;

        potionFree.SingleSessionSearch = true;
        PopulateSingleSessionTotals(potionFree);

        bool potionFreeWon = IsCompleteVictory(potionFree);
        if (!potionFreeWon)
        {
            List<SolverResult> searches = [primary, potionFree];
            SolverResult selected = primary;
            FrontierContinuationScheduler continuationScheduler = new(context);
            IReadOnlyList<PlanAction> openingPotions = new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile,
                    SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: primary.PotionCount)
                .BuildPreferredOpeningPotionActions();
            foreach (PlanAction openingPotion in openingPotions)
            {
                SolverResult posterior = continuationScheduler.Dispatch(
                    new ContinuationSearchRequest(context,
                        ContinuationPurpose.RequiredOpeningPotion,
                        [openingPotion], profile, SolverPotionPolicy.RequireAtLeastOne,
                        primary.PotionCount, null)
                    { ResetFixedPrefixSchedulingBaseline = false });
                if (posterior.ResultScope != SolverResultScope.SearchCompletion)
                    return posterior;

                posterior.SingleSessionSearch = true;
                PopulateSingleSessionTotals(posterior);
                searches.Add(posterior);
                if (HasReachedAcceptableBattleHpLoss(policy, posterior))
                {
                    MergeAuditTotals(posterior, searches.ToArray());
                    return posterior;
                }

                bool posteriorWon = posterior.Snapshot.AllEnemiesDead
                    && !posterior.Snapshot.PlayerDead
                    && posterior.Snapshot.ProjectedPlayerHp > 0;
                int posteriorDeficit = StrategicHpDeficit(root, policy, posterior);
                if (IsBetterCompletedResult(root, policy, posterior, selected))
                {
                    selected = posterior;
                }
                policy.Diagnostics.Info(
                    $"[CombatSolver/Test] REQUIRED_MULTI_POTION_POSTERIOR " +
                    $"potion={openingPotion.PotionId} target={openingPotion.TargetCombatId?.ToString() ?? "-"} " +
                    $"won={posteriorWon} hp_deficit={posteriorDeficit} " +
                    $"selected={ReferenceEquals(selected, posterior)}");

                if (primary.PotionCount != 2)
                    continue;

                IReadOnlyList<PlanAction> secondPotions = new CombatBeamSolver(
                        root,
                        displayNames,
                        battleDamage,
                        policy,
                        cancellationToken,
                        progressCallback,
                        profile,
                        SolverPotionPolicy.RequireAtLeastOne,
                        maximumPotionUses: primary.PotionCount)
                    .BuildPreferredPotionActionsAfterPrefix([openingPotion]);
                foreach (PlanAction secondPotion in secondPotions)
                {
                    SolverResult pairPosterior = continuationScheduler.Dispatch(
                        new ContinuationSearchRequest(context,
                            ContinuationPurpose.RequiredPotionPair,
                            [openingPotion, secondPotion], profile,
                            SolverPotionPolicy.RequireAtLeastOne, primary.PotionCount, null)
                        { ResetFixedPrefixSchedulingBaseline = false });
                    if (pairPosterior.ResultScope != SolverResultScope.SearchCompletion)
                        return pairPosterior;

                    pairPosterior.SingleSessionSearch = true;
                    PopulateSingleSessionTotals(pairPosterior);
                    searches.Add(pairPosterior);
                    if (HasReachedAcceptableBattleHpLoss(policy, pairPosterior))
                    {
                        MergeAuditTotals(pairPosterior, searches.ToArray());
                        return pairPosterior;
                    }

                    bool pairWon = pairPosterior.Snapshot.AllEnemiesDead
                        && !pairPosterior.Snapshot.PlayerDead
                        && pairPosterior.Snapshot.ProjectedPlayerHp > 0;
                    int pairDeficit = StrategicHpDeficit(root, policy, pairPosterior);
                    if (IsBetterCompletedResult(root, policy, pairPosterior, selected))
                    {
                        selected = pairPosterior;
                    }
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] REQUIRED_POTION_PAIR_POSTERIOR " +
                        $"first={openingPotion.PotionId}:{openingPotion.TargetCombatId?.ToString() ?? "-"} " +
                        $"second={secondPotion.PotionId}:{secondPotion.TargetCombatId?.ToString() ?? "-"} " +
                        $"won={pairWon} hp_deficit={pairDeficit} " +
                        $"selected={ReferenceEquals(selected, pairPosterior)}");

                    int selectedDeficit = StrategicHpDeficit(root, policy, selected);
                    if (!pairWon || pairDeficit > selectedDeficit + 1)
                        continue;

                    PlanAction[] pairPrefix = [openingPotion, secondPotion];
                    PlanAction? defensiveFollowUp = new CombatBeamSolver(
                            root,
                            displayNames,
                            battleDamage,
                            policy,
                            cancellationToken,
                            progressCallback,
                            profile,
                            SolverPotionPolicy.RequireAtLeastOne,
                            maximumPotionUses: primary.PotionCount)
                        .BuildOpeningDefensiveFollowUp(pairPrefix);
                    if (defensiveFollowUp == null)
                        continue;

                    SolverResult defensivePosterior = continuationScheduler.Dispatch(
                        new ContinuationSearchRequest(context,
                            ContinuationPurpose.RequiredPotionPairDefensive,
                            [openingPotion, secondPotion, defensiveFollowUp], profile,
                            SolverPotionPolicy.RequireAtLeastOne, primary.PotionCount, null)
                        { ResetFixedPrefixSchedulingBaseline = false });
                    if (defensivePosterior.ResultScope != SolverResultScope.SearchCompletion)
                        return defensivePosterior;

                    defensivePosterior.SingleSessionSearch = true;
                    PopulateSingleSessionTotals(defensivePosterior);
                    searches.Add(defensivePosterior);
                    if (HasReachedAcceptableBattleHpLoss(policy, defensivePosterior))
                    {
                        MergeAuditTotals(defensivePosterior, searches.ToArray());
                        return defensivePosterior;
                    }

                    bool defensiveWon = defensivePosterior.Snapshot.AllEnemiesDead
                        && !defensivePosterior.Snapshot.PlayerDead
                        && defensivePosterior.Snapshot.ProjectedPlayerHp > 0;
                    int defensiveDeficit = StrategicHpDeficit(root, policy, defensivePosterior);
                    if (IsBetterCompletedResult(root, policy, defensivePosterior, selected))
                    {
                        selected = defensivePosterior;
                    }
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] REQUIRED_POTION_PAIR_DEFENSIVE_POSTERIOR " +
                        $"first={openingPotion.PotionId}:{openingPotion.TargetCombatId?.ToString() ?? "-"} " +
                        $"second={secondPotion.PotionId}:{secondPotion.TargetCombatId?.ToString() ?? "-"} " +
                        $"follow_up={defensiveFollowUp.CardId} won={defensiveWon} " +
                        $"hp_deficit={defensiveDeficit} " +
                        $"selected={ReferenceEquals(selected, defensivePosterior)}");
                }
            }

            MergeAuditTotals(selected, searches.ToArray());
            policy.Diagnostics.Info(
                "[CombatSolver/Test] REQUIRED_POTION_AUDIT result potion_free_won=False " +
                $"selected={(ReferenceEquals(selected, primary) ? "multi_potion_rescue" : "opening_potion_posterior")}");
            return selected;
        }

        // The candidates this baseline is compared against are ranked on the strategic axis, so the
        // baseline has to be measured on it too; the raw sum here predated healing counting at all.
        PotionFreePolicyBaseline baseline = new(
            Won: true,
            HpDeficit: StrategicHpDeficit(root, policy, potionFree),
            PlayerHp: potionFree.Snapshot.PlayerHp,
            CombatEndedTurn: potionFree.CombatEndedTurn)
        {
            DeathSaveUseCount = potionFree.Snapshot.ProjectedDeathSaveUseCount,
        };
        SolverResult audited = new CombatBeamSolver(
            root,
            displayNames,
            battleDamage,
            policy,
            cancellationToken,
            progressCallback,
            profile,
            SolverPotionPolicy.RequireAtLeastOne,
            baseline,
            maximumPotionUses: 1).Solve();
        if (audited.ResultScope != SolverResultScope.SearchCompletion)
            return audited;

        audited.SingleSessionSearch = true;
        PopulateSingleSessionTotals(audited);
        SolverResult auditedSelection = IsBetterPotionPolicyResult(
            root,
            policy,
            audited,
            primary)
                ? audited
                : primary;
        MergeAuditTotals(auditedSelection, primary, potionFree, audited);
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] REQUIRED_POTION_AUDIT result potion_free_won=True " +
            $"baseline_hp_deficit={baseline.HpDeficit} " +
            $"selected={(ReferenceEquals(auditedSelection, audited) ? "single_potion_audit" : "primary")} " +
            $"selected_potion_count={auditedSelection.PotionCount} " +
            $"selected_saved={auditedSelection.PotionHpSaved} " +
            $"selected_required={auditedSelection.PotionHpRequired}");
        return auditedSelection;
    }

    private static SolverResult AuditSmartPotionUse(
        SearchPassContext context,
        CancellationToken callerCancellationToken,
        SolverResult primary,
        SmartLayerMemoryForecast memoryForecast)
    {
        CombatRootSnapshot root = context.Root;
        SolverDisplayNames displayNames = context.DisplayNames;
        BattleDamageSnapshot battleDamage = context.BattleDamage;
        SearchPolicySnapshot policy = context.Policy;
        CancellationToken searchCancellationToken = context.CancellationToken;
        Action<SolverProgress>? progressCallback = context.ProgressCallback;
        SolverSearchProfile profile = context.Profile;
        Action<SolverResult>? interimResultCallback = context.InterimResultCallback;
        if (policy.PotionPolicy != SolverPotionPolicy.Smart)
            return primary;
        try
        {
            SolverResult gradient = SearchSmartPotionGradient(
                context, callerCancellationToken, primary, memoryForecast);
            if (gradient.ResultScope != SolverResultScope.SearchCompletion
                || policy.PotionStrategy.HasForcedDirectives
                || battleDamage.PotionsUsedSoFar != 0
                || (gradient.ExplicitPotionCount <= 1
                    && HasReachedProvablePrimaryQualityLowerBound(root, policy, gradient)))
                return gradient;

            CombatBeamSolver builder = new(root, displayNames, battleDamage, policy,
                searchCancellationToken, progressCallback, profile,
                potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                maximumPotionUses: 1);
            IReadOnlyList<PlanAction> openingPotions = builder.BuildOpeningPotionActions();
            IReadOnlyList<PlanAction> generatedPotions = builder.SelectGeneratedCardPotionActions(openingPotions);
            bool openingBlockPotionCanPayForItself = root.Forecast.Rounds.FirstOrDefault()?
                .SelectMany(move => move.AttackHits)
                .Sum(hit => hit.Damage) >= SolverWeights.PotionMinimumHpSaved;
            List<PlanAction[]> prefixes = openingPotions
                .Where(potion => potion.Choice == null
                    && (PotionUsePolicy.RequiresOpeningUse(potion.PotionId)
                        || potion.PotionId == "BLOCK_POTION" && openingBlockPotionCanPayForItself))
                .GroupBy(potion => potion.PotionSlot)
                .Select(group => new[] { group.First() })
                .Concat(generatedPotions.Select(potion => new[] { potion }))
                .Concat(openingPotions
                    .Where(potion => potion.Choice?.Effect == PlanChoiceEffect.SetFreeThisCombat)
                    .Take(8)
                    .Select(potion => new[] { potion }))
                .ToList();
            foreach (PlanAction[] openingPotion in prefixes.ToArray())
            {
                if (openingPotion[0].PotionId == "BLOCK_POTION")
                {
                    foreach (PlanAction attack in builder
                                 .BuildOpeningOffensiveCardVariantsAfterPrefix(openingPotion)
                                 .Take(5))
                    {
                        PlanAction[] attackPrefix = [.. openingPotion, attack];
                        prefixes.Add(attackPrefix);
                        foreach (PlanAction followUpAttack in builder
                                     .BuildOpeningOffensiveCardVariantsAfterPrefix(attackPrefix))
                        {
                            PlanAction[] offensivePrefix = [.. attackPrefix, followUpAttack];
                            PlanAction? defense = builder.BuildOpeningDefensiveFollowUp(offensivePrefix);
                            if (defense == null)
                                continue;
                            prefixes.Add([.. offensivePrefix, defense]);
                            break;
                        }
                    }
                }
                foreach (PlanAction power in builder.BuildPowerActionsAfterPrefix(openingPotion)
                             .Where(action => PowerCardValuationModels.Registry.ContainsCardId(action.CardId!))
                             .Take(2))
                {
                    PlanAction[] powerPrefix = [.. openingPotion, power];
                    prefixes.Add(powerPrefix);
                    PlanAction? defense = builder.BuildOpeningDefensiveFollowUp(powerPrefix);
                    if (defense == null)
                        continue;
                    PlanAction[] defendedPrefix = [.. powerPrefix, defense];
                    foreach (PlanAction setup in builder.BuildOpeningHandSetupActions(defendedPrefix).Take(2))
                        prefixes.Add([.. defendedPrefix, setup]);
                }
                if (openingPotion[0].Choice == null
                    && PotionUsePolicy.RequiresOpeningUse(openingPotion[0].PotionId))
                {
                    foreach (PlanAction draw in builder.BuildOpeningHandSetupActions(openingPotion).Take(1))
                    {
                        PlanAction[] drawnPrefix = [.. openingPotion, draw];
                        foreach (PlanAction attack in builder.BuildOpeningOffensiveCardVariantsAfterPrefix(drawnPrefix))
                            prefixes.Add([.. drawnPrefix, attack]);
                    }
                }
                if (openingPotion[0].Choice?.Effect == PlanChoiceEffect.SetFreeThisCombat)
                {
                    foreach (PlanAction setup in builder.BuildOpeningHandSetupActions(openingPotion).Take(1))
                    {
                        PlanAction[] setupPrefix = [.. openingPotion, setup];
                        foreach (PlanAction attack in builder.BuildOpeningOffensiveFollowUps(setupPrefix))
                            prefixes.Add([.. setupPrefix, attack]);
                    }
                }
            }
            foreach (PlanAction[] synergy in builder.BuildOpeningPowerPotionSynergyPrefixes())
            {
                PlanAction? setup = builder.BuildOpeningSetupFollowUp(synergy);
                if (setup == null)
                {
                    prefixes.Add(synergy);
                    continue;
                }
                prefixes.Add([.. synergy, setup]);
            }
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SMART_OPENING_POTION_PREFIXES " +
                $"generated={generatedPotions.Count} total={prefixes.Count}");
            SolverResult selected = gradient;
            int prefixLimit = prefixes.Any(prefix => prefix[0].PotionId == "BLOCK_POTION"
                || prefix[0].Choice?.Effect == PlanChoiceEffect.SetFreeThisCombat) ? 12 : 8;
            FrontierContinuationScheduler continuationScheduler = new(context);
            foreach (PlanAction[] prefix in prefixes
                         .DistinctBy(PowerPrefixKey)
                         .Take(prefixLimit))
            {
                string prefixText = string.Join('+', prefix.Select(action =>
                    action.Kind == PlanActionKind.UsePotion
                        ? $"POTION:{action.PotionId}@{action.PotionSlot}" +
                          (action.Choice?.Cards.FirstOrDefault() is { } chosen
                              ? $":{chosen.CardId}" : "")
                        : action.CardId));
                SolverSearchProfile routeProfile = prefix.Length > 1
                    ? profile with
                    {
                        MaxExpandedNodes = Math.Min(profile.MaxExpandedNodes, 120_000),
                        SoftTimeBudgetMilliseconds = Math.Min(profile.SoftTimeBudgetMilliseconds, 60_000),
                        BaseScoreOnly = prefix[0].Choice?.Effect != PlanChoiceEffect.SetFreeThisCombat,
                        BeamWidth = prefix[0].Choice?.Effect == PlanChoiceEffect.SetFreeThisCombat
                            ? Math.Min(512, profile.BeamWidth * 3)
                            : BeamWidthPortfolio.ScaledWidth(
                                profile.BeamWidth, BeamWidthPortfolio.WideRefinementRatio),
                    }
                    : profile;
                SolverResult? candidate = continuationScheduler.DispatchOptional(
                    new ContinuationSearchRequest(context,
                        ContinuationPurpose.SmartOpeningPotionPosterior,
                        prefix, routeProfile, SolverPotionPolicy.RequireAtLeastOne,
                        prefix[0].PotionId == "BLOCK_POTION"
                            ? Math.Min(2, MaximumSmartPotionUses(root, policy,
                                potionFreeWon: false, potionFreeHpDeficit: 0))
                            : 1, null)
                    { ResetFixedPrefixSchedulingBaseline = false },
                    $"SMART_OPENING_POTION_POSTERIOR prefix={prefixText}");
                if (candidate == null)
                    continue;
                if (candidate.ResultScope != SolverResultScope.SearchCompletion)
                    return candidate;
                PopulateSingleSessionTotals(candidate);
                bool won = IsCompleteVictory(candidate);
                int saved = IsCompleteVictory(primary)
                    ? Math.Max(0, StrategicHpDeficit(root, policy, primary)
                        - StrategicHpDeficit(root, policy, candidate))
                    : won ? Math.Max(0, candidate.Snapshot.PlayerHp - primary.Snapshot.PlayerHp) : 0;
                int required = SmartPotionHpRequired(root, policy, candidate);
                bool acceptable = IsSmartPotionGradientCandidateAcceptable(
                    IsCompleteVictory(primary), won, saved, required,
                    policy.TheftPolicy == SolverTheftPolicy.PreserveResources
                        && candidate.OutstandingStolenResource < primary.OutstandingStolenResource);
                bool improved = acceptable && IsBetterSmartPotionAuditResult(
                    root, policy, candidate, selected);
                if (improved)
                {
                    candidate.PotionHpSaved = saved;
                    candidate.PotionHpRequired = required;
                    selected = candidate;
                }
                policy.Diagnostics.Info(
                    $"[CombatSolver/Test] SMART_OPENING_POTION_POSTERIOR " +
                    $"prefix={prefixText} " +
                    $"won={won} hp_deficit={StrategicHpDeficit(root, policy, candidate)} " +
                    $"saved={saved} required={required} selected={improved} " +
                    $"first_turn={string.Join(',', candidate.BestNode.Actions
                        .TakeWhile(action => action.Turn == root.StartTurnNumber)
                        .Select(action => action.Kind == PlanActionKind.PlayCard
                            ? action.CardId : action.Kind.ToString()))}");
                if (selected.ExplicitPotionCount <= 1
                    && HasReachedProvablePrimaryQualityLowerBound(root, policy, selected))
                    break;
            }
            return selected;
        }
        catch (PotionPolicyUnsatisfiedException)
            when (policy.PotionPolicy == SolverPotionPolicy.Smart
                && !policy.PotionStrategy.HasForcedDirectives)
        {
            policy.Diagnostics.Info(
                "[CombatSolver/Test] SMART_POTION_AUDIT result optional_route_missing=true selected=primary");
            return primary;
        }
    }

    private static bool IsBetterSmartPotionAuditResult(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult candidate,
        SolverResult current)
    {
        if (!IsCompleteVictory(candidate) || !IsCompleteVictory(current)
            || candidate.Snapshot.StrategyGoalHpCredit != current.Snapshot.StrategyGoalHpCredit
            || policy.TheftPolicy == SolverTheftPolicy.PreserveResources)
            return IsBetterCompletedResult(root, policy, candidate, current);

        int candidateCost = StrategicHpDeficit(root, policy, candidate)
            + SmartPotionHpRequired(root, policy, candidate);
        int currentCost = StrategicHpDeficit(root, policy, current)
            + SmartPotionHpRequired(root, policy, current);
        return candidateCost != currentCost
            ? candidateCost < currentCost
            : IsBetterCompletedResult(root, policy, candidate, current);
    }

    private static SolverResult SearchSmartPotionGradient(
        SearchPassContext context,
        CancellationToken callerCancellationToken,
        SolverResult potionFree,
        SmartLayerMemoryForecast memoryForecast)
    {
        CombatRootSnapshot root = context.Root;
        SolverDisplayNames displayNames = context.DisplayNames;
        BattleDamageSnapshot battleDamage = context.BattleDamage;
        SearchPolicySnapshot policy = context.Policy;
        CancellationToken searchCancellationToken = context.CancellationToken;
        Action<SolverProgress>? progressCallback = context.ProgressCallback;
        SolverSearchProfile profile = context.Profile;
        Action<SolverResult>? interimResultCallback = context.InterimResultCallback;
        int forcedPotionCount = policy.PotionStrategy.ForcedDirectiveCount;
        if (potionFree.ExplicitPotionCount != forcedPotionCount)
            throw new InvalidOperationException("Smart 梯度搜索必须从仅满足强制用药的结果开始。");

        bool potionFreeWon = potionFree.Snapshot.AllEnemiesDead
            && !potionFree.Snapshot.PlayerDead
            && potionFree.Snapshot.ProjectedPlayerHp > 0;
        int potionFreeDeficit = StrategicHpDeficit(root, policy, potionFree);
        int maximumOptionalPotionUses = MaximumSmartPotionUses(
            root,
            policy,
            potionFreeWon,
            potionFreeDeficit);
        if (maximumOptionalPotionUses == 0)
        {
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SMART_POTION_GRADIENT result " +
                $"stop=no_potion_acceptable hp_deficit={potionFreeDeficit} maximum=0");
            return potionFree;
        }

        PotionFreePolicyBaseline baseline = new(
            potionFreeWon,
            potionFreeDeficit,
            potionFree.Snapshot.PlayerHp,
            potionFree.CombatEndedTurn)
        {
            DeathSaveUseCount = potionFree.Snapshot.ProjectedDeathSaveUseCount,
        };
        List<SolverResult> searches = [potionFree];
        SolverResult selected = potionFree;
        bool deadlineExpired = false;
        bool acceptablePotionLayerFound = false;
        for (int optionalPotionCount = 1; optionalPotionCount <= maximumOptionalPotionUses; optionalPotionCount++)
        {
            int potionCount = forcedPotionCount + optionalPotionCount;
            if (searchCancellationToken.IsCancellationRequested)
            {
                callerCancellationToken.ThrowIfCancellationRequested();
                deadlineExpired = true;
                break;
            }
            try
            {
                ReclaimAtPotionGradientBoundary(
                    context,
                    potionFree,
                    memoryForecast,
                    potionCount - 1,
                    potionCount);
            }
            catch (OperationCanceledException)
                when (searchCancellationToken.IsCancellationRequested
                    && !callerCancellationToken.IsCancellationRequested)
            {
                deadlineExpired = true;
                break;
            }
            PrimarySearchIncumbent? primaryIncumbent = BuildPrimarySearchIncumbent(
                root,
                policy,
                selected);
            long layerAllocatedAtStart = GC.GetTotalAllocatedBytes(precise: false);
            long layerTransitionsAtStart = context.Budget.WorkTotals.Snapshot().TransitionCount;
            SolverResult? observedLayerResult = null;
            SolverResult candidate;
            try
            {
                candidate = new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    searchCancellationToken,
                    progressCallback,
                    profile,
                    SolverPotionPolicy.RequireAtLeastOne,
                    baseline,
                    maximumPotionUses: potionCount,
                    minimumPotionUses: potionCount,
                    primaryIncumbent: primaryIncumbent).Solve();
                observedLayerResult = candidate;
            }
            catch (PotionPolicyUnsatisfiedException)
            {
                policy.Diagnostics.Info(
                    $"[CombatSolver/Test] SMART_POTION_GRADIENT layer={potionCount} route_missing=true");
                continue;
            }
            catch (OperationCanceledException)
                when (searchCancellationToken.IsCancellationRequested
                    && !callerCancellationToken.IsCancellationRequested)
            {
                deadlineExpired = true;
                break;
            }
            finally
            {
                // Request totals include a solver that failed or was canceled. Use its actual
                // interval, never the selected route's work paired with another layer's bytes.
                ObserveSmartLayerMemory(
                    context, memoryForecast, layerAllocatedAtStart, layerTransitionsAtStart,
                    observedLayerResult, profile, potionCount);
            }
            if (candidate.ResultScope != SolverResultScope.SearchCompletion)
                return candidate;

            candidate.SingleSessionSearch = true;
            PopulateSingleSessionTotals(candidate);
            searches.Add(candidate);
            interimResultCallback?.Invoke(candidate);

            bool candidateWon = IsCompleteVictory(candidate);
            int candidateDeficit = StrategicHpDeficit(root, policy, candidate);
            int hpSaved = potionFreeWon
                ? Math.Max(0, potionFreeDeficit - candidateDeficit)
                : candidateWon
                    ? Math.Max(0, candidate.Snapshot.PlayerHp - potionFree.Snapshot.PlayerHp)
                    : 0;
            int hpRequired = SmartPotionHpRequired(root, policy, candidate);
            bool protectsLoot = policy.TheftPolicy == SolverTheftPolicy.PreserveResources
                && candidate.OutstandingStolenResource < potionFree.OutstandingStolenResource;
            bool acceptable = IsSmartPotionGradientCandidateAcceptable(
                potionFreeWon,
                candidateWon,
                hpSaved,
                hpRequired,
                protectsLoot);
            bool improvesSelection = acceptable
                && IsBetterPotionPolicyResult(root, policy, candidate, selected);
            if (improvesSelection)
            {
                candidate.PotionHpSaved = hpSaved;
                candidate.PotionHpRequired = hpRequired;
                selected = candidate;
                acceptablePotionLayerFound = true;
            }
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SMART_POTION_GRADIENT layer={potionCount} " +
                $"won={candidateWon} hp_deficit={candidateDeficit} saved={hpSaved} " +
                $"required={hpRequired} protects_loot={protectsLoot} acceptable={acceptable} " +
                $"selected={improvesSelection} " +
                $"expanded={candidate.ExpandedNodes} transitions={candidate.TransitionCount} " +
                $"choice_branches={candidate.ChoiceBranchesEvaluated} " +
                $"elapsed_ms={candidate.Elapsed.TotalMilliseconds:F1} " +
                $"allocated_bytes={candidate.WorkerAllocatedBytes} " +
                $"incumbent_deficit={primaryIncumbent?.StrategicHpDeficit.ToString() ?? "-"} " +
                $"incumbent_turn={primaryIncumbent?.CombatEndedTurn.ToString() ?? "-"} " +
                $"incumbent_pruned={candidate.PrimaryIncumbentBranchesPruned} " +
                $"incumbent_updates={candidate.PrimaryIncumbentUpdates}");
            if (HasReachedAcceptableBattleHpLoss(policy, selected)
                && TheftEncounterStrategy.RecoverySatisfied(
                    policy.TheftPolicy, selected.OutstandingStolenResource))
                break;
        }

        callerCancellationToken.ThrowIfCancellationRequested();
        MergeAuditTotals(selected, [.. searches]);
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] SMART_POTION_GRADIENT result " +
            $"stop={(deadlineExpired ? "deadline" : acceptablePotionLayerFound ? "threshold_met" : "complete")} " +
            $"maximum={forcedPotionCount + maximumOptionalPotionUses} " +
            $"selected_potions={selected.PotionCount}");
        return selected;
    }

    internal static bool IsSmartPotionGradientCandidateAcceptable(
        bool potionFreeWon,
        bool candidateWon,
        int hpSaved,
        int hpRequired,
        bool protectsLoot)
        => candidateWon
            && (!potionFreeWon || hpSaved >= hpRequired || protectsLoot);

    private static void ObserveSmartLayerMemory(
        SearchPassContext context,
        SmartLayerMemoryForecast forecast,
        long processAllocatedAtStart,
        long transitionsAtStart,
        SolverResult? result,
        SolverSearchProfile profile,
        int completedPotionCount)
    {
        SearchPolicySnapshot policy = context.Policy;
        if (policy.PotionPolicy != SolverPotionPolicy.Smart)
            return;
        long processAllocated = Math.Max(
            0,
            GC.GetTotalAllocatedBytes(precise: false) - processAllocatedAtStart);
        long transitions = Math.Max(
            0,
            context.Budget.WorkTotals.Snapshot().TransitionCount - transitionsAtStart);
        // A fixed node budget is a comparable work window for the next layer using this same
        // profile. A timed-out or interrupted layer can understate that window, so keep the
        // optional reset conservative until a complete observation is available again.
        bool usableSample = result is { ResultScope: SolverResultScope.SearchCompletion }
            && result.BoundaryReason != SearchBoundaryReason.TimeLimit
            && result.Elapsed.TotalMilliseconds < profile.SoftTimeBudgetMilliseconds;
        forecast.Observe(processAllocated, transitions, usableSample);
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] SMART_LAYER_MEMORY_SAMPLE layer={completedPotionCount} " +
            $"process_allocated_bytes={processAllocated} transitions={transitions} " +
            $"sample_usable={usableSample.ToString().ToLowerInvariant()} " +
            $"boundary={result?.BoundaryReason.ToString() ?? "incomplete"} " +
            $"bytes_per_transition_high_water={forecast.BytesPerTransitionHighWater:F1} " +
            $"prediction_error_high_water={forecast.UnderpredictionHighWater:F3}");
    }

    private static void ReclaimAtPotionGradientBoundary(
        SearchPassContext context,
        SolverResult totalsCarrier,
        SmartLayerMemoryForecast forecast,
        int completedPotionCount,
        int nextPotionCount)
    {
        SearchPolicySnapshot policy = context.Policy;
        CancellationToken cancellationToken = context.CancellationToken;
        Action<SolverProgress>? progressCallback = context.ProgressCallback;
        SolverSearchProfile profile = context.Profile;
        SearchMemoryPressureSignal signal = policy.MemoryPressureSignal;
        cancellationToken.ThrowIfCancellationRequested();
        SmartLayerMemoryDecision decision = forecast.Decide(
            signal.IsEnabled,
            signal.HasUnexpectedNoGcLoss(),
            signal.AllocatedBytes,
            signal.RemainingBytes,
            signal.AllocationLimitBytes);
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] POTION_GRADIENT_MEMORY_DECISION " +
            $"completed_layer={completedPotionCount} next_layer={nextPotionCount} " +
            $"reclaim={decision.ShouldReclaim.ToString().ToLowerInvariant()} reason={decision.Reason} " +
            $"forecast_bytes={decision.ForecastBytes} remaining_bytes={decision.RemainingBytes} " +
            $"observations={forecast.ObservationCount} minimum_transition_growth=2 allocation_safety_factor=1.5");
        if (!decision.ShouldReclaim)
            return;

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        TimeSpan pauseBefore = GC.GetTotalPauseDuration();
        SearchGcLifecycleSnapshot lifecycleBefore = signal.CaptureGcLifecycle();
        Stopwatch stopwatch = Stopwatch.StartNew();
        progressCallback?.Invoke(new SolverProgress(
            totalsCarrier.StartTurnNumber,
            totalsCarrier.StartTurnNumber + Math.Max(0, totalsCarrier.SearchedTurns - 1),
            totalsCarrier.SearchedTurns,
            PlayDepth: 0,
            // A memory reset is a coordinator-owned interval between solvers. Publish a
            // zero-based interval so the request progress accumulator closes the preceding
            // solver exactly once and does not count potionFree again before every layer.
            ExpandedNodes: 0,
            ReviewedWorldlines: 0,
            MaxNodes: profile.MaxExpandedNodes,
            FrontierNodes: 0,
            EndedNodes: 1,
            ElapsedMilliseconds: 0,
            Phase: "切换用药路线，正在整理内存"));
        long pressureBefore = signal.AllocatedBytes;
        long limitBefore = signal.AllocationLimitBytes;
        try
        {
            signal.ReclaimAndContinue(cancellationToken, "smart_potion_layer");
        }
        finally
        {
            // ReclaimWithinSearch can observe a deadline after completing its blocking Gen2.
            // Retain that completed work in request totals even when cancellation then unwinds.
            stopwatch.Stop();
            TimeSpan gcPause = GC.GetTotalPauseDuration() - pauseBefore;
            TimeSpan maxObservedGcPause = signal.LastReclaimMaxObservedGcPause;
            long allocatedBytes = Math.Max(
                0,
                GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
            int gen0Collections = GC.CollectionCount(0) - gen0Before;
            int gen1Collections = GC.CollectionCount(1) - gen1Before;
            int gen2Collections = GC.CollectionCount(2) - gen2Before;
            totalsCarrier.TotalWorkerAllocatedBytes = checked(
                totalsCarrier.TotalWorkerAllocatedBytes
                + allocatedBytes);
            totalsCarrier.TotalGen0Collections += gen0Collections;
            totalsCarrier.TotalGen1Collections += gen1Collections;
            totalsCarrier.TotalGen2Collections += gen2Collections;
            totalsCarrier.TotalGcPauseDuration += gcPause;
            if (maxObservedGcPause > totalsCarrier.TotalMaxObservedGcPause)
                totalsCarrier.TotalMaxObservedGcPause = maxObservedGcPause;
            totalsCarrier.TotalSearchElapsed += stopwatch.Elapsed;
            context.Budget.WorkTotals.RecordCoordinatorOverhead(
                stopwatch.Elapsed,
                allocatedBytes,
                gen0Collections,
                gen1Collections,
                gen2Collections,
                gcPause,
                maxObservedGcPause);

            policy.Diagnostics.Info(
                $"[CombatSolver/Test] POTION_GRADIENT_MEMORY_RESET " +
                $"completed_layer={completedPotionCount} next_layer={nextPotionCount} " +
                $"allocated_before={pressureBefore} limit_before={limitBefore} " +
                $"allocated_after={signal.AllocatedBytes} limit_after={signal.AllocationLimitBytes} " +
                $"gc_pause_ms={gcPause.TotalMilliseconds:F1} " +
                $"max_observed_gc_pause_ms={maxObservedGcPause.TotalMilliseconds:F1} " +
                signal.CaptureGcLifecycle().DeltaFrom(lifecycleBefore).ToDiagnosticString() + " " +
                $"elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F1} " +
                $"canceled={cancellationToken.IsCancellationRequested.ToString().ToLowerInvariant()}");
        }
    }

    internal static SolverInterimResult CapturePortfolioQuality(
        CombatRootSnapshot root, SearchPolicySnapshot policy, SolverResult result)
        => BuildInterimResult(root, policy, result);

    private static SolverInterimResult BuildInterimResult(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult result)
        => new(
            Won: IsCompleteVictory(result),
            OutstandingStolenResource: result.OutstandingStolenResource,
            ProjectedBattleHpLost: result.ProjectedBattleHpLost,
            StrategicHpDeficit: StrategicHpDeficit(root, policy, result),
            PotionStrategicCost: SmartPotionHpRequired(root, policy, result),
            ProjectedBattlePotionCount: result.ProjectedBattlePotionCount,
            CombatEndedTurn: result.CombatEndedTurn,
            EnemyHp: result.Snapshot.EnemyHp,
            Score: result.BestNode.Score)
        {
            GrowthHpCredit = result.Snapshot.StrategyGoalHpCredit,
            TheftPolicy = policy.TheftPolicy,
            GrowthRewardCount = result.Snapshot.StrategyGoalCount,
            Survives = !result.Snapshot.PlayerDead && result.Snapshot.ProjectedPlayerHp > 0,
            DeathSaveUseCount = result.Snapshot.ProjectedDeathSaveUseCount,
        };


    private static bool IsBetterCompletedResult(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult candidate,
        SolverResult current)
    {
        int primaryQuality = CompareCompletedResultPrimaryQuality(root, policy, candidate, current);
        if (primaryQuality != 0)
            return primaryQuality < 0;
        return candidate.PotionCount < current.PotionCount
            || candidate.PotionCount == current.PotionCount
                && candidate.BestNode.Score > current.BestNode.Score;
    }

    private static bool IsBetterPotionPolicyResult(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult candidate,
        SolverResult current)
        => IsBetterPotionPolicyResult(
            policy.TheftPolicy,
            BuildInterimResult(root, policy, candidate),
            BuildInterimResult(root, policy, current));

    internal static bool IsBetterPotionPolicyResult(
        SolverTheftPolicy? theftPolicy,
        SolverInterimResult candidate,
        SolverInterimResult current)
        => RouteQualityPolicy.Compare(
            RouteQuality.FromInterim(candidate), RouteQuality.FromInterim(current),
            RouteQualityProjection.PotionPolicy, theftPolicy) < 0;

    private static int CompareCompletedResultPrimaryQuality(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult candidate,
        SolverResult current)
    {
        bool candidateWon = IsCompleteVictory(candidate);
        bool currentWon = IsCompleteVictory(current);
        int victoryComparison = currentWon.CompareTo(candidateWon);
        if (victoryComparison != 0)
            return victoryComparison;
        bool candidateSurvives = !candidate.Snapshot.PlayerDead
            && candidate.Snapshot.ProjectedPlayerHp > 0;
        bool currentSurvives = !current.Snapshot.PlayerDead
            && current.Snapshot.ProjectedPlayerHp > 0;
        int survivalComparison = currentSurvives.CompareTo(candidateSurvives);
        if (survivalComparison != 0)
            return survivalComparison;
        int deathSaveComparison = candidate.Snapshot.ProjectedDeathSaveUseCount.CompareTo(
            current.Snapshot.ProjectedDeathSaveUseCount);
        if (deathSaveComparison != 0)
            return deathSaveComparison;
        int recovery = TheftEncounterStrategy.CompareRecovery(policy.TheftPolicy,
            candidateWon, candidate.OutstandingStolenResource,
            currentWon, current.OutstandingStolenResource);
        if (recovery != 0)
            return recovery;
        return RouteQualityPolicy.Compare(
            RouteQuality.Primary(candidateWon, StrategicHpDeficit(root, policy, candidate),
                candidate.CombatEndedTurn, candidate.Snapshot.StrategyGoalHpCredit,
                candidate.Snapshot.StrategyGoalCount, candidate.Snapshot.ProjectedDeathSaveUseCount),
            RouteQuality.Primary(currentWon, StrategicHpDeficit(root, policy, current),
                current.CombatEndedTurn, current.Snapshot.StrategyGoalHpCredit,
                current.Snapshot.StrategyGoalCount, current.Snapshot.ProjectedDeathSaveUseCount),
            RouteQualityProjection.Primary);
    }

    private static bool IsCompleteVictory(SolverResult result)
        => SolverInterimResultOrdering.IsCompleteVictory(
            result.BestNode.ActionCount,
            result.Snapshot.AllEnemiesDead,
            result.Snapshot.PlayerDead,
            result.Snapshot.ProjectedPlayerHp);

    internal static bool HasReachedAcceptableBattleHpLoss(
        SearchPolicySnapshot policy,
        SolverResult result)
        => policy.GrowthTargetSatisfied(result.Snapshot.GrowthRewards)
            && policy.RelicTargetsSatisfied(result.Snapshot.RelicCounters)
            && TheftEncounterStrategy.RecoverySatisfied(policy.TheftPolicy, result.OutstandingStolenResource)
            && result.Snapshot.ProjectedDeathSaveUseCount == 0
            && result.PotionCount == policy.MinimumRequiredPotionUses(result.BattlePotionsUsedSoFar)
            && policy.PotionStrategy.EvaluateForcedUses(result.BestNode.Actions, renewablePotionShapedRock: false).AllForcedUsesSatisfied
            && HasReachedAcceptableBattleHpLoss(
            IsCompleteVictory(result),
            result.ProjectedBattleHpLost,
            policy.AcceptableBattleHpLoss);

    // This honors an explicitly enabled satisficing policy, not a proof that no
    // alternate route can heal more or finish sooner. Preserve the selected incumbent.
    private static bool CanFinishTargetPortfolio(
        CombatRootSnapshot root, SearchPolicySnapshot policy, SolverSearchProfile profile, SolverResult result)
        => profile.StopPortfolioAtHpTarget
            && !root.HasVisibleHealingSource
            && result.Snapshot.RecoveredPlayerHp == 0
            && result.ResultScope == SolverResultScope.SearchCompletion
            && !result.Snapshot.HasRisk
            && HasReachedAcceptableBattleHpLoss(policy, result);

    internal static bool HasReachedAcceptableBattleHpLoss(
        bool completeVictory,
        int projectedBattleHpLost,
        int acceptableBattleHpLoss)
        => completeVictory && projectedBattleHpLost <= acceptableBattleHpLoss;

    private static bool HasReachedProvablePrimaryQualityLowerBound(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult result)
        => !policy.EffectiveHasGrowthTargets
            && policy.RelicTargets.Count == 0
            && result.Snapshot.ProjectedDeathSaveUseCount == 0
            && TheftEncounterStrategy.RecoverySatisfied(policy.TheftPolicy, result.OutstandingStolenResource)
            && HasReachedProvablePrimaryQualityLowerBound(
            IsCompleteVictory(result),
            StrategicHpDeficit(root, policy, result),
            result.CombatEndedTurn,
            root.StartTurnNumber,
            ProvableStrategicHpFloor(root, policy));

    internal static bool HasReachedProvablePrimaryQualityLowerBound(
        bool completeVictory,
        int strategicHpDeficit,
        int? combatEndedTurn,
        int? earliestPossibleCombatEndedTurn,
        int provableStrategicHpFloor)
    {
        if (earliestPossibleCombatEndedTurn is not { } earliestTurn)
            return false;
        return SolverInterimResultOrdering.ComparePrimaryQuality(
            completeVictory,
            strategicHpDeficit,
            combatEndedTurn,
            currentCompleteVictory: true,
            currentStrategicHpDeficit: provableStrategicHpFloor,
            currentCombatEndedTurn: earliestTurn) <= 0;
    }

    private static PrimarySearchIncumbent? BuildPrimarySearchIncumbent(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult result)
    {
        if (policy.EffectiveHasGrowthTargets
            || policy.RelicTargets.Count > 0
            || result.Snapshot.ProjectedDeathSaveUseCount > 0
            || !IsCompleteVictory(result)
            || result.CombatEndedTurn is not { } combatEndedTurn)
            return null;
        return new PrimarySearchIncumbent(
            StrategicHpDeficit(root, policy, result),
            combatEndedTurn);
    }

    private static SolverResult? SolveOptionalPotionPosterior(
        CombatBeamSolver solver,
        SearchPolicySnapshot policy,
        string diagnostic)
    {
        try
        {
            return solver.Solve();
        }
        catch (PotionPolicyUnsatisfiedException)
        {
            policy.Diagnostics.Info($"[CombatSolver/Test] {diagnostic} qualified=false");
            return null;
        }
    }

    private static int SmartPotionHpRequired(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult result)
    {
        ForcedPotionUseEvaluation forced = policy.PotionStrategy.EvaluateForcedUses(
            result.PotionUses);
        int ambergrisCount = result.BestNode.Actions.Count(action =>
            action.Kind == PlanActionKind.UsePotion
            && string.Equals(action.PotionId, "AMBERGRIS", StringComparison.Ordinal))
            - forced.ForcedAmbergrisCount;
        int explicitPotionCount = result.BestNode.Actions.Count(action =>
            action.Kind == PlanActionKind.UsePotion) - forced.ForcedUseCount;
        int strategicHpCost = PotionUsePolicy.EffectiveStrategicHpCost(
            PotionUsePolicy.ApplyReplacementCredit(
                Math.Max(0, result.PotionStrategicCostByTurn.Values.Sum() - forced.ForcedStrategicHpCost),
                explicitPotionCount,
                root.PotionRewardOutlook.ReplacementHpCredit),
            ambergrisCount,
            root.InitialPlayerMaxHp);
        return PotionUsePolicy.SmartRequiredHpSaved(
            strategicHpCost,
            StrategicBossHpRelief(root, policy));
    }

    private static int StrategicHpDeficit(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult result)
        => ActEndingBossPolicy.StrategicHpDeficit(
            result.Snapshot.CumulativePlayerHpLost,
            Math.Max(0, root.InitialPlayerMaxHp - result.Snapshot.PlayerMaxHp),
            result.Snapshot.RecoveredPlayerHp
                + ActEndingBossPolicy.RankedPostCombatRelicHeal(
                    root.PostCombatRelicHeal,
                    SolverInterimResultOrdering.IsCompleteVictory(
                        result.BestNode.ActionCount,
                        result.Snapshot.AllEnemiesDead,
                        result.Snapshot.PlayerDead,
                        result.Snapshot.ProjectedPlayerHp),
                    result.Snapshot.PlayerHp,
                    result.Snapshot.PlayerMaxHp),
            StrategicBossHpRelief(root, policy),
            result.Snapshot.DeathSaveHpRestored) - result.Snapshot.StrategicHpCredit;

    /// <summary>
    /// Best strategic HP result any route could still reach from this root.
    /// </summary>
    /// <remarks>
    /// Once healing counts, zero is no longer the floor. Current HP is capped by max HP, so a route can at most
    /// heal back to full, which puts the floor at the HP the player was already missing when the fight started.
    /// Treating zero as the floor while a wounded player holds a heal would declare a route provably optimal
    /// when a strictly better one exists, and stop the extra searches that would have found it.
    /// </remarks>
    private static int ProvableStrategicHpFloor(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy)
        => -ActEndingBossPolicy.PersistentValueOfRecoveredHp(
            Math.Max(0, root.InitialPlayerMaxHp - root.InitialPlayerHp),
            StrategicBossHpRelief(root, policy));

    internal static bool CanAnySmartPotionQualify(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        bool potionFreeWon,
        int potionFreeHpDeficit)
        => MaximumSmartPotionUses(root, policy, potionFreeWon, potionFreeHpDeficit) > 0;

    internal static int MaximumSmartPotionUses(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        bool potionFreeWon,
        int potionFreeHpDeficit)
    {
        SearchablePotionSlotSnapshot[] allowedPotions = root.SearchablePotions
            .Where(potion => policy.PotionStrategy.AllowsExplicitUse(
                potion.Slot,
                potion.PotionId,
                SolverPotionPolicy.Smart,
                forceAllDisabled: false)
                && policy.PotionStrategy.Resolve(potion.Slot, potion.PotionId)
                    != SolverPotionDirective.Force)
            .ToArray();
        int generatedPotionCapacity = allowedPotions.Any(potion =>
            potion.PotionId == "ENTROPIC_BREW")
            ? root.PotionSlotCount
            : 0;
        int searchablePotionUses = allowedPotions.Length + generatedPotionCapacity;
        if (!potionFreeWon || policy.TheftPolicy == SolverTheftPolicy.PreserveResources)
            return searchablePotionUses;
        BossHpRelief bossHpRelief = StrategicBossHpRelief(root, policy);
        int paidPotionHpRequired = PotionUsePolicy.SmartRequiredHpSaved(
            SolverWeights.PotionMinimumHpSaved,
            bossHpRelief);
        // The reward credit is taken off a route once, so only the first paid potion gets the cheaper bar.
        int firstPaidPotionHpRequired = PotionUsePolicy.SmartRequiredHpSaved(
            PotionUsePolicy.ApplyReplacementCredit(
                SolverWeights.PotionMinimumHpSaved,
                1,
                root.PotionRewardOutlook.ReplacementHpCredit),
            bossHpRelief);
        int paidPotionCapacity = paidPotionHpRequired >= int.MaxValue / 4
            ? 0
            : Math.Max(0, potionFreeHpDeficit) < firstPaidPotionHpRequired
                ? 0
                : 1 + (Math.Max(0, potionFreeHpDeficit) - firstPaidPotionHpRequired) / paidPotionHpRequired;
        return Math.Min(
            searchablePotionUses,
            allowedPotions.Count(potion => potion.StrategicHpCost == 0)
                + paidPotionCapacity
                + (paidPotionCapacity > 0 ? generatedPotionCapacity : 0));
    }

    private static BossHpRelief StrategicBossHpRelief(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy)
        => ActEndingBossPolicy.ResolveStrategicHpRelief(
            root.BossHpRelief,
            policy.ActTransitionBossHpStrategy,
            policy.FinalBossHpStrategy);

    private static void MergeAuditTotals(
        SolverResult selected,
        params SolverResult[] searches)
    {
        if (searches.Length == 0)
            throw new ArgumentException("审计总量至少需要一个搜索结果。", nameof(searches));

        SearchRequestWorkSnapshot totals = AggregateAuditWork(
            searches.Select(AuditWorkContribution).ToArray());
        PopulateRequestWorkTotals(selected, totals);
        // This result spans an audit even when a future caller supplies one layer.
        // Preserve the historical coordinator-session classification.
        selected.SingleSessionSearch = false;
    }

    private static SearchSolverWorkContribution AuditWorkContribution(SolverResult result)
        => new(
            result.ExpandedNodes,
            result.TransitionCount,
            result.ChoiceBranchesEvaluated,
            result.TotalSearchElapsed,
            result.TotalWorkerAllocatedBytes,
            result.TotalGen0Collections,
            result.TotalGen1Collections,
            result.TotalGen2Collections,
            result.TotalGcPauseDuration,
            result.TotalMaxObservedGcPause);

    internal static SearchRequestWorkSnapshot AggregateAuditWork(
        params SearchSolverWorkContribution[] searches)
    {
        SearchRequestWorkTotals totals = new();
        foreach (SearchSolverWorkContribution search in searches)
            totals.Record(search);
        return totals.Snapshot();
    }

    private static void PopulateRequestWorkTotals(
        SolverResult result,
        SearchRequestWorkTotals requestWorkTotals)
        => PopulateRequestWorkTotals(result, requestWorkTotals.Snapshot());

    private static void PopulateRequestWorkTotals(
        SolverResult result,
        SearchRequestWorkSnapshot totals)
    {
        result.TotalCycleReplayActions = totals.CycleReplayActions;
        result.SingleSessionSearch = totals.RecordedSolverCount == 1;
        result.TotalSearchElapsed = totals.Elapsed;
        result.TotalWorkerAllocatedBytes = totals.WorkerAllocatedBytes;
        result.TotalGen0Collections = SaturatingInt(totals.Gen0Collections);
        result.TotalGen1Collections = SaturatingInt(totals.Gen1Collections);
        result.TotalGen2Collections = SaturatingInt(totals.Gen2Collections);
        result.TotalGcPauseDuration = totals.GcPauseDuration;
        result.TotalMaxObservedGcPause = totals.MaxObservedGcPause;
        result.TotalExpandedNodes = totals.ExpandedNodes;
        result.TotalTransitionCount = totals.TransitionCount;
        result.TotalChoiceBranchesEvaluated = totals.ChoiceBranchesEvaluated;
    }

    private static int SaturatingInt(long value)
        => value >= int.MaxValue ? int.MaxValue : (int)value;

    private static void PopulateSingleSessionTotals(
        SolverResult result)
    {
        result.TotalSearchElapsed = result.Elapsed;
        result.TotalWorkerAllocatedBytes = result.WorkerAllocatedBytes;
        result.TotalGen0Collections = result.Gen0Collections;
        result.TotalGen1Collections = result.Gen1Collections;
        result.TotalGen2Collections = result.Gen2Collections;
        result.TotalGcPauseDuration = result.GcPauseDuration;
        result.TotalMaxObservedGcPause = result.MaxObservedGcPause;
        result.TotalExpandedNodes = result.ExpandedNodes;
        result.TotalTransitionCount = result.TransitionCount;
        result.TotalCycleReplayActions = result.CycleReplayActions;
        result.TotalChoiceBranchesEvaluated = result.ChoiceBranchesEvaluated;
    }
}
