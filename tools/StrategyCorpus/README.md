# Strategy refactor corpus

Run from the CombatSolver repository root after building the Release Mod and OfflineSearchHarness:

```powershell
python tools/StrategyCorpus/run.py --manifest coverage/strategy-refactor-p0/corpus.json --out .local/strategy-refactor-p0/baseline
python tools/StrategyCorpus/compare.py --left .local/strategy-refactor-p0/baseline --right .local/strategy-refactor-p0/after --out .local/strategy-refactor-p0/comparison
```

The runner restores each report from `combat_start` through the platform's unattended launcher and runs generated fixtures through OfflineSearchHarness. It uses a fixed VeryHigh profile with 25,000 nodes per solver, DOP 1, a 110-second search budget, and no development script or experimental early-turn search. The launcher has a 180-second outer deadline for startup and cleanup. It reuses one managed headless instance across reports and cleans it on the final report. A failed or timed-out case is recorded once; backups 56 and 63 are attempted in order only when a primary report is unavailable.

`case.json` contains exact input identity, the selected route, complete choice data, quality axes, request totals and pruning counters. The source ZIP and full evidence stay under `.local`. A case is comparable only when native state and continuation restoration both pass and the search has no time boundary. The comparator rejects changed roots or policies, reports the first deterministic difference by field, and classifies the result using the frozen `SolverInterimResultOrdering.IsBetter` rules from 0.47.0. Wall time, memory and GC are observational. The fixed-budget corpus is a structural regression gate, not a claim that it reproduces the previously published 180-second search outcomes.
