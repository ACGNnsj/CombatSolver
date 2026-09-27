using System.Reflection;
using System.Text.Json.Nodes;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertFixedPrefixTurnOutcomesAsync(CombatState combat, Player player)
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Fixed-prefix outcomes: " + message);
        }
        static void Reject(Action action, string expectedMessage)
        {
            try { action(); }
            catch (InvalidOperationException error) when (error.Message.Contains(expectedMessage, StringComparison.Ordinal)) { return; }
            throw new InvalidOperationException("Fixed-prefix outcomes: expected rejection " + expectedMessage);
        }

        await ClearPlayerPilesAsync(player);
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray())
            await PowerCmd.Remove(power);
        await CreatureCmd.SetCurrentHp(player.Creature, player.Creature.MaxHp);
        await CreatureCmd.SetCurrentHp(combat.Enemies.Single(), 50);
        await InjectCardAsync(combat, player, new UnattendedCardInjection
        {
            CardId = "STRIKE_IRONCLAD", Pile = "Hand", DynamicVars = new() { ["Damage"] = 100 },
        });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_IRONCLAD", Pile = "Hand" });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "WOUND", Pile = "Draw", Count = 3 });
        SetEnergy(player, 3);
        int firstTurn = player.PlayerCombatState!.TurnNumber;
        string liveBefore = ContinuationStamp.CaptureLive(combat).StateText;
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
            SolverSettings.Capture(), combat, false, null) with
        {
            FixedBudget = true, VerifyIncrementalSearch = true, DetailedDiagnostics = false,
            MaxDegreeOfParallelism = 1, BudgetOverrideMilliseconds = 5000,
            PotionPolicy = SolverPotionPolicy.Disabled,
            PotionStrategy = new PotionStrategySnapshot(SolverPotionPolicy.Disabled, []),
            Profile = SolverSearchProfile.Default with { BeamWidth = 8, MaxExpandedNodes = 100, SoftTimeBudgetMilliseconds = 5000 },
        };
        PlanAction[] turns = Enumerable.Range(firstTurn, 3)
            .Select(turn => new PlanAction(PlanActionKind.EndTurn, turn)).ToArray();
        PlanAction strike = new(PlanActionKind.PlayCard, firstTurn + 3,
            CardId: "STRIKE_IRONCLAD", TargetCombatId: combat.Enemies.Single().CombatId);
        PlanAction defend = new(PlanActionKind.PlayCard, firstTurn + 3, CardId: "DEFEND_IRONCLAD");
        SolverResult Solve(IReadOnlyList<PlanAction> prefix, bool reset = false)
            => new CombatBeamSolver(root, names, damage, policy, searchProfile: policy.Profile,
                fixedPrefixActions: prefix, resetFixedPrefixSchedulingBaseline: reset).Solve();

        CombatBeamSolver inspection = new(root, names, damage, policy, searchProfile: policy.Profile);
        SimulationSnapshot seedSnapshot = InvokeForcedTerminalReplay(inspection, [], null, 0, null);
        SearchNode seed = new(null, 0, 0, 0, firstTurn, SearchRouteTraits.None, 0,
            seedSnapshot.Score, seedSnapshot.StateKey, seedSnapshot.HasRisk, seedSnapshot.BoundaryReason,
            false, null, seedSnapshot, CombatProgressState.Capture(seedSnapshot));
        MethodInfo applyPrefix = typeof(CombatBeamSolver).GetMethod("ApplyFixedPrefix",
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(SearchNode), typeof(IReadOnlyList<PlanAction>), typeof(bool)], null)!;
        MethodInfo annotations = typeof(CombatBeamSolver).GetMethod("BuildRouteAnnotations",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        SearchNode? inspected = null;
        try
        {
            inspected = (SearchNode)applyPrefix.Invoke(inspection, [seed, turns, false])!;
            for (SearchNode? node = inspected; node?.Parent != null; node = node.Parent)
                Check(node.Outcome?.Turn == node.Action!.Turn, "prefix owns outcome before projection");
            SearchNode rawFallback = inspected with
            {
                Outcome = null, FutureSoldHp = inspected.Parent!.FutureSoldHp + 2,
            };
            object projected = annotations.Invoke(inspection, [rawFallback, null, null])!;
            var projectedSold = (IReadOnlyDictionary<int, int>)projected.GetType()
                .GetProperty("SoldHpByTurn")!.GetValue(projected)!;
            Check(projectedSold[firstTurn + 2] == 2 && rawFallback.Outcome == null
                && rawFallback.Score == inspected.Score, "raw fallback projection preserves score and sold delta");
            TurnOutcome existing = inspected.Outcome! with { MaxBlock = 123 };
            SearchNode ranked = inspected with { Outcome = existing };
            object retained = annotations.Invoke(inspection, [ranked, null, null])!;
            var retainedBlock = (IReadOnlyDictionary<int, int>)retained.GetType()
                .GetProperty("MaxBlockByTurn")!.GetValue(retained)!;
            Check(retainedBlock[firstTurn + 2] == 123, "existing comparative annotation remains authoritative");
        }
        finally
        {
            inspected?.Snapshot.ReleaseSimulator();
            seedSnapshot.ReleaseSimulator();
        }
        _completedChecks.Add("FixedPrefixOutcomes:NodeOwnedBeforeProjection:RawFallbackSoldDelta:ExistingOutcomePreserved");

        SolverResult result = await Task.Run(() => Solve(turns));
        Check(result.Snapshot.AllEnemiesDead && result.CombatEndedTurn == firstTurn + 3, "searched tail must win on fourth turn");
        Check(result.TryValidateTurnOutcomes(out _), "completed prefix tables");
        Check(result.HpLostByTurn.ContainsKey(firstTurn + 1) && result.HpLostByTurn[firstTurn + 1] == 0,
            "explicit zero-loss prefix turn");
        Check(result.HpLostByTurn[firstTurn + 2] > 0, "third prefix turn must lose HP");
        Check(result.HpLostByTurn.Values.Sum() == result.Snapshot.CumulativePlayerHpLost, "prefix and tail cumulative loss");
        Check(result.SoldHpByTurn.Values.Sum() == result.FutureSoldHp, "comparative sold HP unchanged");
        SolverResult terminal = await Task.Run(() => Solve([.. turns, strike], reset: true));
        Check(terminal.HpLostByTurn.OrderBy(pair => pair.Key).SequenceEqual(result.HpLostByTurn.OrderBy(pair => pair.Key)),
            "terminal prefix and searched tail losses");
        SolverResult partial = await Task.Run(() => Solve([.. turns, defend]));
        Check(partial.HpLostByTurn.Count == 4 && partial.ActualBlockByTurn[firstTurn + 3] == 5,
            "partial final prefix annotated once after suffix");
        Check(partial.EnergyLeftByTurn[firstTurn + 3] == 1, "partial prefix and suffix energy");
        Check((await Task.Run(() => Solve([]))).HpLostByTurn.ContainsKey(firstTurn), "empty-prefix ordinary search");
        await Task.Run(() => Reject(() => Solve([.. turns, strike, new PlanAction(PlanActionKind.EndTurn, firstTurn + 3)]),
            "回放包含已锁定战斗终局之后的动作"));
        await Task.Run(() => Reject(() => Solve([new PlanAction(PlanActionKind.EndTurn, firstTurn + 1)]), "固定搜索前缀动作无效"));
        await Task.Run(() => Reject(() => Solve([new PlanAction(PlanActionKind.PlayCard, firstTurn,
            CardId: "STRIKE_IRONCLAD", EndsPlayerTurn: true)]), "固定搜索前缀动作无效"));
        Check(ContinuationStamp.CaptureLive(combat).StateText == liveBefore, "search changed live root");
        _completedChecks.Add("FixedPrefixOutcomes:ThreeTurns:ZeroAndPositiveLoss:PartialTail:Terminal:Empty:InvalidSuffix:RootUnchanged");

        byte[] validBytes = SolvedRouteCache.SerializeRoute(result);
        SolverResult copy = SolvedRouteCache.DeserializeRoute(validBytes, root.Forecast);
        Check(copy.HpLostByTurn.OrderBy(pair => pair.Key).SequenceEqual(result.HpLostByTurn.OrderBy(pair => pair.Key)), "cache roundtrip");
        string[] fields = [nameof(SolverResult.HpLostByTurn), nameof(SolverResult.HpRecoveredByTurn),
            nameof(SolverResult.EnemyHpLostByTurn), nameof(SolverResult.SoldHpByTurn),
            nameof(SolverResult.MaxBlockByTurn), nameof(SolverResult.ActualBlockByTurn), nameof(SolverResult.EnergyLeftByTurn)];
        string directory = Path.Combine(Godot.ProjectSettings.GlobalizePath("user://"), "prefix-outcome-checks");
        Directory.CreateDirectory(directory);
        foreach (string field in fields)
        {
            JsonObject invalid = JsonNode.Parse(validBytes)!.AsObject();
            invalid[field]!.AsObject().Remove((firstTurn + 2).ToString());
            string path = Path.Combine(directory, field + ".json");
            string text = invalid.ToJsonString();
            File.WriteAllText(path, text);
            Check(new SolvedRouteCache(path).Read(root.Forecast) == null && File.ReadAllText(path) == text,
                "incomplete disk cache must be a preserved miss: " + field);
            bool rejected = false;
            try { SolvedRouteCache.DeserializeRoute(System.Text.Encoding.UTF8.GetBytes(text), root.Forecast); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "incomplete imported route: " + field);
        }
        Dictionary<int, int> losses = (Dictionary<int, int>)result.HpLostByTurn;
        int thirdLoss = losses[firstTurn + 2];
        losses.Remove(firstTurn + 2);
        try
        {
            Reject(() => result.RequireHpLostForTurn(firstTurn + 2), "不能按零伤害执行");
            Reject(() => SolvedRouteCache.SerializeRoute(result), "路线回合统计不完整");
            Reject(() => result.TryCreateContinuation(result.Continuations[0].ExpectedState, 80, damage, out _),
                "路线回合统计不完整");
        }
        finally { losses[firstTurn + 2] = thirdLoss; }
        _completedChecks.Add("FixedPrefixOutcomes:SevenTables:CacheRoundtrip:InvalidCachePreserved:ImportAndContinuationRejected:MissingLossNotZero");

        for (int turn = firstTurn; turn < firstTurn + 3; turn++)
        {
            LiveEndTurnRiskProjection risk = LiveEndTurnRiskEvaluator.Evaluate(combat, null);
            Check(risk.HpLost == result.RequireHpLostForTurn(turn), $"risk recheck turn {turn}: {risk.HpLost}");
            CombatManager.Instance.OnEndedTurnLocally();
            EndPlayerTurnAction end = new(player, turn);
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(end);
            await end.CompletionTask;
            while (player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } current || current.TurnNumber <= turn)
            {
                EnsureWithinDeadline();
                await NextFrameAsync();
            }
            ContinuationStamp actual = ContinuationStamp.CaptureLive(combat);
            CachedContinuation expected = result.Continuations.Single(item => item.StartTurnNumber == turn + 1);
            Check(expected.ExpectedState == actual, $"native continuation turn {turn + 1}: {expected.ExpectedState.DescribeFirstDifference(actual)}");
            Check(result.TryCreateContinuation(actual, player.Creature.CurrentHp,
                BattleDamageTracker.Observe(combat), out SolverResult? continuation), "native route reuse");
            Check(continuation!.RequireHpLostForTurn(turn + 1) == result.RequireHpLostForTurn(turn + 1), "reuse retains prefix outcomes");
            var display = SolverOverlaySnapshot.BattleHpTotalsForDisplay(0, 0, result.HpLostByTurn,
                result.HpRecoveredByTurn, firstTurn);
            Check(display.ProjectedLoss == result.Snapshot.CumulativePlayerHpLost, "display includes prefix loss");
        }
        _completedChecks.Add("FixedPrefixOutcomes:NativeThreeTurnContinuation:LiveEndTurnRiskMatches:ReuseAndDisplayTotals");
    }
}
