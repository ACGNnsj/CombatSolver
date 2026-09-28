using System.Runtime;

namespace CombatSolver;

internal static partial class SearchGcPolicy
{
    private static long _noGcRecoveryGeneration;
    private static long _searchRecoveryBudgetCapBytes;
    private static bool IsRecoverableNoGcOutcome(NoGcRegionStartOutcome outcome)
        => outcome is NoGcRegionStartOutcome.InsufficientMemory
            or NoGcRegionStartOutcome.SystemHeadroomInsufficient
            or NoGcRegionStartOutcome.SkippedAfterUnexpectedLoss;

    private static void InstallNoGcRecoveryProbe(
        SearchMemoryPressureSignal signal, long configuredBudget, long configuredLohBudget)
    {
        NoGcRecoveryBackoff backoff = new();
        long generation = ++_noGcRecoveryGeneration;
        _searchRecoveryBudgetCapBytes = 0;
        void ObserveFallback(long completedGen2Index)
        {
            if (completedGen2Index > 0)
                backoff.ArmReclaimedFallback(Environment.TickCount64, completedGen2Index);
            else
                backoff.ArmFallback(Environment.TickCount64, CaptureCompletedGen2Index());
        }
        signal.SetNoGcRecoveryProbe((reservedBytes, cancellationToken) =>
        {
            long now = Environment.TickCount64;
            if (!backoff.ShouldObserve(now))
                return;
            GCMemoryInfo memory = GC.GetGCMemoryInfo(GCKind.Any);
            long gen2Index = CaptureCompletedGen2Index();
            if (!backoff.ObserveCompletedCollection(now, gen2Index))
                return;

            lock (Gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (generation != _noGcRecoveryGeneration
                    || _activeSearches != 1 || _defaultGcSearches != 0 || _noGcRegionActive
                    || _reclaimActive || _reclaimRequested || _manualReclaimRequested
                    || _regionExitOnlyRequested || !_regionExitOnlyTask.IsCompleted
                    || GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
                    return;

                long systemLimit = ResolveSystemMemoryLimit(memory);
                long load = PhysicalMemoryUsage.Capture(memory).UsedBytes;
                long budget = RecoveryBudget(configuredBudget, systemLimit, load);
                long lohBudget = Math.Min(configuredLohBudget, Math.Max(1, budget / 6));
                long allocationLimit = Math.Min((budget - lohBudget) / 5 * 4, budget / 4 * 3);
                // Do not establish a region that the already-known next atomic operation
                // cannot fit. Total Gen2 fragmentation is not guaranteed NoGC SOH capacity.
                if (budget < MinimumNoGcRegionBudgetBytes || reservedBytes > allocationLimit)
                    return;

                backoff.RecordAttempt(now, gen2Index);
                GCLatencyMode previous = GCSettings.LatencyMode;
                NoGcRegionStartOutcome outcome = TryStartNoGcRegion(budget, lohBudget, restart: true);
                if (outcome == NoGcRegionStartOutcome.Started)
                {
                    _previousMode = previous;
                    _latencyModeOwned = true;
                    _noGcRegionActive = true;
                    _configuredNoGcRegionBudgetBytes = configuredBudget;
                    _configuredNoGcRegionLohBudgetBytes = configuredLohBudget;
                    _noGcRegionBudgetBytes = budget;
                    _noGcRegionLohBudgetBytes = lohBudget;
                    _noGcRegionAllocatedBytesAtStart = GC.GetTotalAllocatedBytes(false);
                    _lastEstablishedNoGcRegionBudgetBytesForTesting = budget;
                    _searchRecoveryBudgetCapBytes = budget;
                    ConfigureSearchMemoryLimit(signal, _noGcRegionAllocatedBytesAtStart,
                        budget, budget, lohBudget, configuredBudget, configuredLohBudget);
                    backoff.RecordRecovery();
                }
                else if (!IsRecoverableNoGcOutcome(outcome))
                {
                    // A classification the probe cannot act on (for example unsupported region
                    // sizing) still must not strand the running search: keep the allowance so a
                    // later drained boundary can try again under different conditions.
                    signal.UseDefaultGcFallback(systemHeadroomConstrained: false, allowNoGcRecovery: true);
                }
                Entry.Logger.Info($"[CombatSolver/Test] GC_NO_GC_RECOVERY attempt={backoff.Attempts} " +
                    $"outcome={FormatStartOutcome(outcome)} budget={budget} loh_budget={lohBudget} " +
                    $"next_commit_reserve={reservedBytes} physical_load={load} system_limit={systemLimit} " +
                    $"completed_gen2_index={gen2Index} forced_collect=false");
            }
        }, ObserveFallback);
        // Admission can fall back before its scope probe has been installed.
        if (!signal.IsEnabled)
            ObserveFallback(0);
    }

    private static long CaptureCompletedGen2Index()
    {
        GCMemoryInfo background = GC.GetGCMemoryInfo(GCKind.Background);
        GCMemoryInfo blocking = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        return Math.Max(background.Generation == GC.MaxGeneration ? background.Index : 0,
            blocking.Generation == GC.MaxGeneration ? blocking.Index : 0);
    }

    internal static long RecoveryBudget(long configured, long systemLimit, long memoryLoad)
    {
        if (systemLimit == long.MaxValue)
            return configured;
        long headroom = Math.Max(0, systemLimit - memoryLoad);
        // Hysteresis: half the current physical headroom stays outside the reservation.
        // Reclaimed Gen2 holes may be useful to normal GC but do not enlarge this allowance.
        return Math.Min(configured, headroom / 2);
    }

    internal sealed class NoGcRecoveryBackoff
    {
        // Recovery is a cooldown, not a one-way door. A long multi-unit encounter can fail
        // several attempts while its retention set keeps growing; stopping after a fixed
        // attempt count would leave the rest of that search with no allocation ceiling at
        // all, which is strictly worse than retrying at a slower rate. The delay saturates
        // instead, so the retry rate stays bounded while the search keeps every chance to
        // re-establish a region.
        private const long MaximumObservationDelayMilliseconds = 60_000;

        // 2_000L << 5 == 64_000 already exceeds the saturated delay, so a small exponent is
        // enough; clamping well below 63 keeps the shift well defined for any attempt count.
        private const int MaximumObservationExponent = 5;

        private long _nextObservation;
        private long _lastGen2Index;
        private bool _armed;
        public int Attempts { get; private set; }

        public bool ShouldObserve(long now) => now >= _nextObservation;

        public void ArmFallback(long now, long gen2Index)
        {
            _armed = true;
            _lastGen2Index = gen2Index;
            // A long atomic interval between fallback and the next drained boundary must
            // count toward the cooldown, without shortening a previous attempt's backoff.
            _nextObservation = Math.Max(_nextObservation, now + 2_000);
        }

        public void ArmReclaimedFallback(long now, long completedGen2Index)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(completedGen2Index, 1);
            _armed = true;
            // The checkpoint already confirmed a collection after the loss. Reuse that
            // evidence instead of allocating under ordinary GC just to wait for another one.
            _lastGen2Index = Math.Max(_lastGen2Index, completedGen2Index - 1);
            // Only the first recovery can be immediate. Later losses retain the previous
            // attempt's backoff and the same per-scope hard cap.
            _nextObservation = Math.Max(_nextObservation, now);
        }

        public bool ObserveCompletedCollection(long now, long gen2Index)
        {
            if (!ShouldObserve(now))
                return false;
            _nextObservation = now + 2_000;
            if (!_armed)
            {
                _armed = true;
                _lastGen2Index = gen2Index;
                return false;
            }
            return gen2Index > _lastGen2Index;
        }

        public void RecordAttempt(long now, long gen2Index)
        {
            Attempts++;
            _lastGen2Index = gen2Index;
            // Attempts is no longer capped, so the shift count must be clamped before it can
            // wrap, and the deadline must saturate instead of overflowing past long.MaxValue.
            int shift = Math.Min(Attempts, MaximumObservationExponent);
            long delay = Math.Min(MaximumObservationDelayMilliseconds, 2_000L << shift);
            _nextObservation = now > long.MaxValue - delay ? long.MaxValue : now + delay;
        }

        public void RecordRecovery() => _armed = false;
    }
}
