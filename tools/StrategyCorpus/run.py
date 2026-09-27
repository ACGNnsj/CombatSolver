#!/usr/bin/env python3
"""Capture one fixed-budget search per corpus root without publishing player data."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys


ROOT = Path(__file__).resolve().parents[2]
HARNESS = ROOT / "tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll"
LAUNCHER = ROOT / "tools/run-unattended-test.ps1" if os.name == "nt" else ROOT / "tools/run-unattended-test.sh"
DISPLAY_KEYS = {"cardTitle", "targetName", "actionTitle", "potionTitle", "title", "summary"}
VOLATILE_METRICS = {
    "capturedAtElapsedMilliseconds", "elapsedMilliseconds", "totalElapsedMilliseconds",
    "firstRoutePublishedMilliseconds", "peakManagedHeapBytes", "allocatedBytes",
    "managedHeapBytesAfter", "workerAllocatedBytes", "totalWorkerAllocatedBytes",
    "managedHeapBytes", "managedLiveBytes", "managedFragmentedBytes", "workingSetBytes",
    "privateMemoryBytes", "totalGen0Collections", "totalGen1Collections",
    "totalGen2Collections", "totalGcPauseMilliseconds", "maxGcPauseMilliseconds",
    "gcLifecycle", "gcLifecycleAttribution", "gcLatencyMode", "noGcRegionActive",
    "noGcRegionBudgetBytes", "noGcRegionRolloverCount", "configuredNoGcRegionEnabled",
    "configuredNoGcRegionBudgetBytes",
}


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def digest(path):
    sha = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            sha.update(chunk)
    return sha.hexdigest()


def normalized(value, excluded):
    if isinstance(value, dict):
        return {key: normalized(item, excluded) for key, item in value.items() if key not in excluded}
    if isinstance(value, list):
        return [normalized(item, excluded) for item in value]
    return value


def launch(command, directory):
    with (directory / "launcher.log").open("w", encoding="utf-8") as output:
        return subprocess.run(command, cwd=ROOT, stdout=output, stderr=subprocess.STDOUT,
                              check=False).returncode


def headless_command(case, source, evidence, manifest, cleanup):
    common = [
        "-CheckpointArchivePath", str(source), "-CheckpointSelector", "start",
        "-ReplayMode", "SearchOnly", "-EvidenceDirectory", str(evidence),
        "-HeadlessInstance", "strategy-refactor-p0", "-FixedSearchBudget",
        "-PerformancePresetForTest", manifest["profile"],
        "-SearchMaxExpandedNodesForTest", str(manifest["maxExpandedNodes"]),
        "-SearchMaxDegreeOfParallelismForTest", str(manifest["maxDegreeOfParallelism"]),
        "-SearchBudgetOverrideMilliseconds", str(manifest["searchBudgetMilliseconds"]),
        "-EnableNoGcRegionForTest", "0", "-TimeoutSeconds", "180",
    ]
    if os.name == "nt":
        command = ["pwsh", "-NoProfile", "-File", str(LAUNCHER), *common]
        if cleanup:
            command.append("-CleanupInstanceOnExit")
        return command
    command = [
        "bash", str(LAUNCHER), "--checkpoint-archive-path", str(source),
        "--checkpoint-selector", "start", "--replay-mode", "SearchOnly",
        "--evidence-directory", str(evidence), "--headless-instance", "strategy-refactor-p0",
        "--fixed-search-budget", "--performance-preset-for-test", manifest["profile"],
        "--search-max-expanded-nodes-for-test", str(manifest["maxExpandedNodes"]),
        "--search-max-degree-of-parallelism-for-test", str(manifest["maxDegreeOfParallelism"]),
        "--search-budget-override-milliseconds", str(manifest["searchBudgetMilliseconds"]),
        "--enable-no-gc-region-for-test", "false", "--timeout-seconds", "180",
    ]
    if cleanup:
        command.append("--cleanup-instance-on-exit")
    return command


def capture_report(case, source, directory, manifest, cleanup):
    evidence = directory / "evidence"
    evidence.mkdir()
    code = launch(headless_command(case, source, evidence, manifest, cleanup), directory)
    result_path = evidence / "result.json"
    result = read(result_path) if result_path.exists() else {}
    verification_path = evidence / "policy.json"
    verification = read(verification_path) if verification_path.exists() else {}
    search_path = evidence / "search-result.json"
    search = read(search_path) if search_path.exists() else {}
    metrics = result.get("solverMetrics") or {}
    ready = (code == 0 and result.get("status") == "Passed"
             and verification.get("restorationVerified") is True
             and verification.get("continuationVerified") is True
             and verification.get("nativeStateVerified") is True
             and metrics.get("boundary") != "TimeLimit"
             and metrics.get("turnLayerTimeBudgetStops", 0) == 0
             and search.get("comparisonQuality") is not None
             and search.get("rootContinuationStamp") is not None)
    checkpoint = verification.get("checkpoint") or {}
    return {
        "status": "comparable" if ready else "unavailable",
        "reason": None if ready else result.get("error") or verification.get("reason")
                  or f"exit={code} status={result.get('status')} restoration={verification.get('restorationVerified')} boundary={metrics.get('boundary')}",
        "identity": {
            "sourceSha256": digest(source), "checkpointId": checkpoint.get("checkpointId"),
            "rootContinuationStamp": search.get("rootContinuationStamp"),
            "gameModuleId": (verification.get("gameModuleComparison") or {}).get("actual"),
            "policy": verification.get("executedPolicy"),
        },
        "provenance": {"reportId": case["reportId"], "modSha256": result.get("mainAssemblyHash")},
        "quality": search.get("comparisonQuality"),
        "outcome": {key: metrics.get(key) for key in (
            "boundary", "onlyDeathRoutes", "projectedBattleHpLost", "potionCount",
            "potionUses", "finalHp", "finalEnemyHp", "combatEndedTurn")},
        "actions": normalized(search.get("actions"), DISPLAY_KEYS),
        "continuations": search.get("continuations"),
        "metrics": normalized(metrics, VOLATILE_METRICS | DISPLAY_KEYS),
        "pruneCounters": search.get("pruneCounters"),
        "observations": {"elapsedMilliseconds": metrics.get("totalElapsedMilliseconds"),
                         "workingSetBytes": result.get("workingSetBytes")},
    }


def capture_generated(case, source, directory, manifest):
    if not HARNESS.is_file():
        raise FileNotFoundError(f"OfflineSearchHarness is not built: {HARNESS}")
    command = [
        "dotnet", str(HARNESS), "--request", str(source), "--label", case["label"],
        "--out", str(directory / "evidence"), "--search-mode", "Coordinator",
        "--use-portfolio", "--profile", manifest["profile"],
        "--nodes", str(manifest["maxExpandedNodes"]),
        "--dop", str(manifest["maxDegreeOfParallelism"]),
        "--budget-ms", str(manifest["searchBudgetMilliseconds"]),
    ]
    (directory / "evidence").mkdir()
    code = launch(command, directory)
    evidence = directory / "evidence"
    result = read(evidence / "result.json") if (evidence / "result.json").exists() else {}
    search = read(evidence / "search-policy.json") if (evidence / "search-policy.json").exists() else None
    route = read(evidence / "route.json") if (evidence / "route.json").exists() else None
    metrics = result.get("solverMetrics") or {}
    ready = (code == 0 and result.get("status") == "Passed"
             and metrics.get("boundary") != "TimeLimit"
             and metrics.get("turnLayerTimeBudgetStops", 0) == 0
             and result.get("comparisonQuality") is not None)
    return {
        "status": "comparable" if ready else "unavailable",
        "reason": None if ready else result.get("error") or f"exit={code} status={result.get('status')} boundary={metrics.get('boundary')}",
        "identity": {"sourceSha256": digest(source),
                     "rootContinuationStamp": result.get("rootContinuationStamp"),
                     "catalogFingerprint": result.get("catalogFingerprint"), "policy": search},
        "provenance": {"request": case["path"]},
        "quality": result.get("comparisonQuality"),
        "outcome": {key: metrics.get(key) for key in (
            "boundary", "onlyDeathRoutes", "projectedBattleHpLost", "potionCount",
            "potionUses", "finalHp", "finalEnemyHp", "combatEndedTurn")},
        "actions": normalized(route, DISPLAY_KEYS),
        "continuations": result.get("continuations"),
        "metrics": normalized(metrics, VOLATILE_METRICS | DISPLAY_KEYS),
        "pruneCounters": normalized(result.get("pruneCounters"), VOLATILE_METRICS),
        "observations": {"wallSeconds": result.get("wallSeconds"),
                         "peakWorkingSetBytes": result.get("peakWorkingSetBytes")},
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    manifest = read(args.manifest)
    if manifest.get("schemaVersion") != 1:
        raise ValueError("Unsupported corpus manifest")
    output = args.out.resolve()
    if output.exists():
        raise FileExistsError(f"Corpus output already exists: {output}")
    output.mkdir(parents=True)
    report_cases = [case for case in manifest["cases"] if case["kind"] == "report" and not case.get("backup")]
    backups = [case for case in manifest["cases"] if case["kind"] == "report" and case.get("backup")]
    generated = [case for case in manifest["cases"] if case["kind"] == "generated"]
    selected = [*report_cases, *generated]
    results = {}
    report_failures = 0
    for case in selected:
        source = ROOT / case["path"]
        directory = output / case["label"]
        directory.mkdir()
        if not source.is_file():
            result = {"status": "unavailable", "reason": f"Missing source: {source}"}
        else:
            try:
                if case["kind"] == "report":
                    remaining_reports = [item for item in selected if item["kind"] == "report" and item["label"] not in results]
                    result = capture_report(case, source, directory, manifest, len(remaining_reports) == 1)
                else:
                    result = capture_generated(case, source, directory, manifest)
            except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
                result = {"status": "unavailable", "reason": f"{type(error).__name__}: {error}"}
        result["label"] = case["label"]
        result["kind"] = case["kind"]
        (directory / "case.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
        results[case["label"]] = result["status"]
        print(f"{case['label']}: {result['status']} {result.get('reason') or ''}", flush=True)
        if case["kind"] == "report" and result["status"] != "comparable":
            report_failures += 1
    for case in backups[:report_failures]:
        source = ROOT / case["path"]
        directory = output / case["label"]
        directory.mkdir()
        try:
            result = capture_report(case, source, directory, manifest, True)
        except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
            result = {"status": "unavailable", "reason": f"{type(error).__name__}: {error}"}
        result["label"] = case["label"]
        result["kind"] = case["kind"]
        (directory / "case.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
        results[case["label"]] = result["status"]
        print(f"{case['label']}: {result['status']} {result.get('reason') or ''}", flush=True)
    (output / "summary.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    comparable_reports = sum(results.get(case["label"]) == "comparable"
                             for case in [*report_cases, *backups])
    comparable_generated = sum(results.get(case["label"]) == "comparable" for case in generated)
    return 0 if comparable_reports >= 4 and comparable_generated == len(generated) else 1


if __name__ == "__main__":
    sys.exit(main())
