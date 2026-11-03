"""Split the e2e suite into duration-balanced shards, and record per-file timings for the next plan.

  plan_e2e_shards.py timings <merged-report.json> <timings.json>
  plan_e2e_shards.py plan <list.json> <timings.json> <shards> <out-dir>

A unit is one spec file in one Playwright project, written as a `--test-list` line. Units are packed
longest-first onto the least-loaded shard; a file without a recorded time weighs its test count times
the median per-test time, so every listed test lands on exactly one shard either way.
"""

from pathlib import Path
import heapq
import json
import os
import statistics
import sys


def units(report):
    """(project › file) → [test count, summed seconds] from a Playwright JSON report or `--list`."""
    found = {}

    def walk(suite):
        for spec in suite.get("specs", []):
            for test in spec["tests"]:
                unit = found.setdefault(f"[{test['projectName']}] › {spec['file']}", [0, 0.0])
                unit[0] += 1
                unit[1] += sum(r["duration"] for r in test.get("results", [])) / 1000
        for child in suite.get("suites", []):
            walk(child)

    for suite in report["suites"]:
        walk(suite)
    return found


def record_timings(report_path, timings_path):
    report = json.loads(Path(report_path).read_text())
    timings = {unit: round(seconds, 1) for unit, (_, seconds) in units(report).items()}
    Path(timings_path).write_text(json.dumps(timings, indent=1, sort_keys=True))


def plan(listed, timings, shards):
    """Returns one sorted unit list per shard and each shard's planned seconds."""
    rates = [timings[u] / listed[u][0] for u in timings if u in listed and timings[u]]
    per_test = statistics.median(rates) if rates else 1.0
    weights = {unit: timings.get(unit, count * per_test) for unit, (count, _) in listed.items()}
    if len(weights) < shards:
        raise SystemExit(f"{shards} shards but only {len(weights)} test files")
    heap = [(0.0, index) for index in range(shards)]
    assigned = [[] for _ in range(shards)]
    for unit in sorted(weights, key=lambda u: (-weights[u], u)):
        load, index = heapq.heappop(heap)
        assigned[index].append(unit)
        heapq.heappush(heap, (load + weights[unit], index))
    if sorted(u for shard in assigned for u in shard) != sorted(listed):
        raise SystemExit("shard plan does not cover the listed tests exactly once")
    return [sorted(shard) for shard in assigned], [sum(weights[u] for u in shard) for shard in assigned]


def write_plan(list_path, timings_path, shards, out_dir):
    listed = units(json.loads(Path(list_path).read_text()))
    timings_file = Path(timings_path)
    timings = json.loads(timings_file.read_text()) if timings_file.exists() else {}
    if not timings:
        print("::warning::No recorded e2e timings; shards are balanced by test count this run.")
    assigned, loads = plan(listed, timings, shards)
    out = Path(out_dir)
    out.mkdir(parents=True, exist_ok=True)
    summary = ["| Shard | Files | Planned seconds |", "|---|---|---|"]
    for index, (shard, load) in enumerate(zip(assigned, loads), start=1):
        (out / f"{index}.txt").write_text("".join(f"{unit}\n" for unit in shard))
        summary.append(f"| {index} | {len(shard)} | {load:.0f} |")
    if "GITHUB_STEP_SUMMARY" in os.environ:
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a") as step_summary:
            step_summary.write("\n".join(["### e2e shard plan", *summary, ""]))
    print("\n".join(summary))


if __name__ == "__main__":
    match sys.argv[1:]:
        case ["timings", report, timings]:
            record_timings(report, timings)
        case ["plan", listed, timings, shards, out]:
            write_plan(listed, timings, int(shards), out)
        case _:
            raise SystemExit(__doc__)
