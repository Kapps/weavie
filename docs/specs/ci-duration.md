# CI duration: measurements and proposals

Status: proposal. Nothing here changes CI config yet.

## Summary

The median green `ci` run takes **15.3 min** from trigger to the `ci` check (p90 19.3 min). The Linux e2e
leg is the critical path in **42 of 42** green runs: bundle (4.2 min) → slowest shard (9.8 min) → report
and `ci` gate (0.6 min). Everything else finishes earlier: `.NET tests` by 7 min, the macOS native build
by 9 min (p90 11 min).

The e2e shards are badly unbalanced. Playwright splits tests by **count**, not duration, so shard 1 runs
3.7 min of tests and shard 6 runs 9.1 min. A duration-balanced split of the same six shards finishes in
7.1 min. More than half of all e2e time (56%) goes to the per-test `weavie` fixture booting and tearing
down a host, not to test bodies.

Dependency caches already hit, so caching is not a lever. The ranked proposals below would take the
median from ~15.3 to ~10 min without dropping any coverage.

## Method

- **Sample**: the last 60 `ci.yml` runs (2026-10-19 → 2026-11-03): 47 completed (37 PR, 10 pushes to
  `main`; 43 green, 4 failed), 12 cancelled (superseded pushes, which `cancel-in-progress` cancels by
  design), and 1 still running. Timing tables use the **42 green, completed runs**. One more green run (41286591728)
  is excluded: every job that ended after 2026-11-01 08:00 UTC reports its end time exactly **+60 min**
  after its last step ended. That is a GitHub timestamp artifact at the US DST change, not a real stall.
- **Sources**: `gh run view --json jobs` for steps; the REST `actions/runs/{id}/jobs` endpoint for
  `created_at` (queue time = `started_at − created_at`); the Playwright blob reports from run 41571164267
  for per-test durations; the job logs of the same run for cache hits and build phases; the last 30
  CodeQL (default setup) runs.
- **Scope**: PR and `main` runs only exercise the `linux` e2e platform. macOS and Windows e2e run only
  when `release.yml` calls `ci` with `full: true`, which isn't measured here.

## Measurements

### Per job (42 green runs)

| Job | Queue p50 | Queue p90 | Run p50 | Run p90 | Max |
|---|---|---|---|---|---|
| e2e (linux) / shard ×6 | 2s | 100s | 7.8m | 9.8m | 19.6m |
| native build (macos) | 3s | 22s | 8.6m | 10.9m | 11.8m |
| .NET tests | 2s | 8s | 6.9m | 7.9m | 8.7m |
| native build (windows) | 2s | 20s | 5.6m | 6.3m | 7.2m |
| native build (linux) | 2s | 38s | 4.5m | 5.2m | 6.5m |
| e2e (linux) / bundle | 2s | 37s | 4.2m | 4.9m | 5.6m |
| web checks | 2s | 10s | 1.0m | 1.1m | 1.2m |
| e2e (linux) / report | 2s | 10s | 0.4m | 0.6m | 0.6m |
| platforms, ci | 2s | 5s | 0.1m | 0.1m | 0.3m |

Whole run, trigger to `ci` check: **p50 15.3 min, p90 19.3 min, max 23.9 min**.

### Critical path (p50 seconds)

| Segment | p50 | p90 |
|---|---|---|
| Trigger → bundle starts (`platforms` + queue) | 11 | 45 |
| Bundle | 255 | 293 |
| Bundle done → slowest shard starts (queue) | 3 | 161 |
| Slowest shard | 590 | 607 |
| Slowest shard done → `ci` done (report + gate) | 35 | 46 |
| **Total** | **918** | **1156** |

```mermaid
gantt
    title Median green ci run (seconds from trigger)
    dateFormat X
    axisFormat %M:%S
    section Critical path
    platforms + queue          :crit, p, 0, 11
    e2e bundle (AOT publish)   :crit, b, 11, 266
    slowest shard (6/6)        :crit, s6, 269, 859
    report + ci gate           :crit, r, 859, 918
    section Other e2e shards
    shard 1/6                  :s1, 269, 538
    shard 3/6                  :s3, 269, 701
    shard 4 and 2/6            :s4, 269, 821
    section Off the critical path
    web checks                 :w, 0, 60
    .NET tests                 :d, 0, 420
    native build (linux)       :nl, 10, 280
    native build (windows)     :nw, 10, 345
    native build (macos)       :nm, 10, 537
```

### Shard imbalance

Playwright with `fullyParallel` shards by contiguous test **count** (`filterForShard` in
playwright 1.61.1), so every shard gets 94–95 tests whatever they cost.

| Shard | Job p50 | Job p90 | Playwright step p50 | Sum of test durations (run 41571164267) |
|---|---|---|---|---|
| 1/6 | 4.5m | 4.9m | 225s | 211s |
| 2/6 | 8.3m | 8.8m | 458s | 473s |
| 3/6 | 7.2m | 7.5m | 390s | 356s |
| 4/6 | 9.2m | 9.4m | 501s | 493s |
| 5/6 | 8.1m | 8.5m | 443s | 482s |
| 6/6 | 9.8m | 10.1m | 546s | 535s |

The gap between the slowest and fastest shard is 5.4 min at p50. Each shard also spends a fixed
**~43s** outside Playwright on checkout, toolchain, pnpm install, bundle download, and browser install.

Simulated makespan (the slowest shard's test time) over the measured per-test durations, 2550s in total:

| Shards | Count split (today's algorithm) | Duration-balanced (LPT, by test) | Duration-balanced (LPT, by file) |
|---|---|---|---|
| 6 | 535s | 425s | 425s |
| 8 | 436s | 319s | 320s |
| 10 | 410s | 255s | 256s |
| 12 | — | 213s | 213s |

Adding shards under the count split barely helps: the expensive review and render-budget specs sort
last and stay together. Balancing **by file** is as good as balancing by test (no file is near the
makespan; the largest is 103s), so it keeps `beforeAll` and file-level `serial` semantics intact.

### Where e2e test time goes (run 41571164267, 565 tests)

| Bucket | Seconds | Share |
|---|---|---|
| `Before Hooks` (mostly `weavie` fixture: host boot to splash cleared) | 1129 | 44% |
| `After Hooks` (host teardown) | 301 | 12% |
| Test bodies | ~1120 | 44% |

The median test takes 3.9s. Only 4 tests run longer than 20s; the slowest is
`unified-review-wheel.spec.ts` › "wheel scrolling preserves file order…" at 48s. Per project:
headless 2032s, chromium 312s, remote 137s, mobile 70s. Each test owns a fresh host by design, and host
reuse was rejected earlier (see the e2e teardown work), so the lever is a cheaper boot, not sharing hosts.

### Inside the bundle job (run 41571164267, `Publish self-contained test bundle` = 230s)

| Phase | Seconds |
|---|---|
| Restore + managed compile (Core, Hosting, Runner) | 56 |
| Runner Native AOT (`Generating native code`) | 32 |
| Worker: managed build + Vite build (13.5s) + hook relay | 45 |
| Headless Native AOT | 85 |
| FakeClaude + FakeAcp | 6 |

These phases run strictly in sequence: `StageHeadlessWorker` is an `Exec` after the Runner's publish,
so the worker's Vite build and AOT compile can't start until the Runner's AOT compile finishes.

### Other heavy steps (p50, off the critical path today)

| Job | Step | p50 | Note |
|---|---|---|---|
| .NET tests | Test Hosting | 138s | |
| .NET tests | Verify .NET formatting | 113s | non-mac projects + whitespace on mac sources |
| .NET tests | Test Core | 52s | each `dotnet test` builds its own graph again |
| native build (macos) | Build (AOT publish) | 219s | p90 315s, a high-variance runner |
| native build (macos) | Verify .NET formatting | 94s | the **whole** solution again, including what Linux already verified |
| native build (windows) | setup-dotnet (NuGet cache extract) | 69s | |
| native build (linux) | Build (AOT publish) | 193s | |

### Caches

Every cache hit on its primary key in the sampled run: pnpm store (`node-cache-*-pnpm-<lock>`) on all
jobs, NuGet (`nuget-<os>-<hash>`) on all .NET jobs, and Playwright browsers (`playwright-Linux-<lock>`)
on all six shards. Step timings agree across all 42 runs: `setup-web` p50 7s, Linux `setup-dotnet` 7s,
shard `pnpm install` 4s, browser install 12s (the remaining cost is `--with-deps` apt work). Better
dependency caching has nothing left to win. What isn't cached is **build output** (managed compile,
ILC, Vite), and that's where the time goes.

### Queueing

Queue time is near zero until about 12 Ubuntu jobs from `ci` are running at once (median 12, max 16,
when a job waited >30s). That pattern matches the account's hosted-runner concurrency cap: one PR run
peaks at ~10 Ubuntu jobs plus macOS, Windows, and 5 CodeQL jobs, so two overlapping pushes contend.
It's why the shard queue p90 is 100s. **Adding shards adds pressure on this cap.**

### Variance and flakes

- **Tail latency is apt.** 11 of 588 apt-touching steps took >60s. The worst, in run 40014261583, was
  `Install Playwright Chromium (Linux)` at 687s on one shard (the run took 24 min). This is the
  mirror stall already described in `e2e-platform.yml`.
- **Failures:** 4 of 46 completed runs failed, each in exactly one e2e test:
  - `acp-background.spec.ts` › "running work survives a page reload…" failed twice, once on a PR and
    once on a `main` push (41370842492). Root-caused and fixed in be104961 (#1126, Restart Agent
    replied before the restart finished). No later occurrences.
  - `web-tab.spec.ts` › "same URL keeps isolated live state in two exact sessions"
    (feature/session-finished-state) and `diff-review.spec.ts` › "a file opened while the review
    computes keeps the editor" (bug/review-branch-stacked-parent) each failed once on a PR branch. This
    data can't tell whether the branch caused them. If either recurs on `main`, it's a flake to
    root-cause under [e2e-flake-policy](e2e-flake-policy.md).
- **CodeQL** (default setup, 5 languages in parallel) takes p50 3.4 min, max 4.5 min for `csharp`.
  It's never on the critical path, but it holds 5 concurrent runners at the start of every PR run.

## Proposals, ranked

Estimated savings are against the p50 total of 15.3 min, cumulative in rank order. None of them
removes or loosens a test, a platform, or the AOT build that ships.

### 1. Balance shards by measured duration: −2.0 min, no extra runners

Plan the shards once per run from the previous green `main` run's per-file durations, using a longest
processing time (LPT) bin-packing, and give each shard an explicit file list instead of `--shard=i/6`.

- **Timing source** (no hand-maintained copy): the `report` job on `main` already merges every shard's
  blob, so it writes `file → seconds` from it and saves the result to `actions/cache` under
  `e2e-timings-<run_id>`. PR runs can read `main`'s caches.
- **Plan once, in one place.** The bundle job already has pnpm deps. It restores the newest
  `e2e-timings-*`, runs `playwright test --list --reporter=json` for the authoritative test set, LPT-packs
  the files, and emits the plan as the shard matrix output. Every shard then runs from the same plan, so
  coverage is complete by construction: no shard can restore a different timings file mid-run and drop
  tests. A unit test asserts that the plan's files partition the `--list` output.
- **New or unknown files** take the median file weight. With no timings cache at all (evicted after 7
  idle days), every file gets equal weight: the result is still a correct, complete plan, just less
  balanced. Surface it as a `::warning` in the job summary rather than failing the run.
- **Result:** the slowest shard goes from 590s to ~470s (425s tests + 43s fixed).
- **Tradeoffs:** ~60 lines of script plus a small change to `report`. Test order across shards changes,
  so an order-dependent leak can surface; fix it, never hide it. Splitting by file keeps `beforeAll`
  semantics.

### 2. Overlap the bundle's two AOT compiles and Vite: −1.0 to −1.5 min

The Runner's AOT (32s) and the whole worker publish (Vite + Headless AOT, ~130s) are independent but
run back to back. Make the Runner's publish build the worker in parallel with its own native compile,
for example by having `StageHeadlessWorker` run the worker publish through an `MSBuild` task that starts
alongside ILC rather than an `Exec` that waits for `Publish`. Keep it inside the Runner project so
"publish the runner, get a complete worker" stays the one staging path that releases and CI share.
Overlapping Vite with the managed compile saves another ~15s.

- **Tradeoffs:** a 4-vCPU runner running two ILC processes at once speeds each up less than linearly;
  measure before claiming the full ~70s. It also touches the release staging path, so a release dry run
  should prove it.

### 3. Move to 8 duration-balanced shards: −1.75 min, +2 Ubuntu runners per run

After #1, eight shards finish in ~365s (320s + 43s) instead of ~470s. Ten would reach ~300s, but the
returns shrink while queue risk grows.

- **Tradeoffs:** peak Ubuntu jobs per run go from ~10 to ~12, past the measured point where queueing
  starts (~12), so two overlapping PR runs will queue more. Adopt it only after #1 and #2. Then measure
  the shard queue p90; if it rises above the ~100s this saves, go back to 6. On a public repo, standard
  runner minutes cost nothing; the cap is concurrency, not money.

### 4. Cut the per-test host boot: ~−50s for each 0.5s saved per test

56% of e2e time is the `weavie` fixture: ~2.0s boot and ~0.5s teardown per test. That's the shipped
Headless AOT host's startup plus the web client's first paint, so the win carries over to real users
too. Profile one fixture boot (process spawn, Kestrel bind, Core graph, wwwroot first paint, splash
clear) and cut the largest phase. Each 0.5s/test saves ~565 × 0.5 / 6 ≈ 47s per shard at 6 shards.

- **Tradeoffs:** unknown effort until profiled. This is product work, not CI config. Keep one host per
  test (isolation is non-negotiable; host reuse was rejected).

### 5. Stop the macOS job re-verifying what Linux already verified: −1.3 min on macOS

`native build (macos)` runs `dotnet format weavie.slnx --verify-no-changes` over the whole solution
(94s), while `.NET tests` already verifies every non-mac project, plus whitespace on the mac sources,
via `format-dotnet.py`. Have macOS verify only the projects Linux can't evaluate (the `-macos` target
frameworks, with full analyzers), and drop the whitespace-only mac pass from Linux. Coverage is the
same, each project gets checked once, and the duplication goes away.

- **Why it matters:** after #1–#3 the e2e path is ~10.5 min, and macOS's p90 (10.9 min) becomes the
  p90 critical path. This keeps it under. A faster macOS image could also help, but `xcode-27` is pinned
  by the workload's Xcode requirement (see `.github/actions/setup-macos-workload`), so that isn't a free
  choice.

### 6. Take apt off the shard path: −10s typical, removes the 3–11 min tail

Every shard runs `playwright install --with-deps chromium`, which is an apt transaction against a mirror
that has stalled for 3–11 min (run 40014261583), wrapped in a 3× retry loop. Run the shards in the
official `mcr.microsoft.com/playwright:v<version>-noble` image instead: the browsers and OS deps are
baked in, so the apt step, the browser cache, and the retry loop all go away.

- **Single source:** compute the tag from `src/web/pnpm-lock.yaml`'s `@playwright/test` version in the
  planning job (#1) and pass it as an output, not as a literal in the workflow.
- **Tradeoffs:** the image pull (~20–30s, cached per runner) replaces ~12s of p50 install, so the typical
  run is about even. The win is the tail (p90 and max). The bundle comes from a different image
  (`ubuntu-latest`), so ldd-check the self-contained AOT binaries in the container once first.

### 7. Build the .NET test graph once: −2 to −3 min of `.NET tests`, off the critical path

Seven sequential `dotnet test <csproj>` calls each re-evaluate and re-build overlapping graphs. A single
`dotnet build` of a test solution filter followed by `dotnet test --no-build` (assemblies in parallel)
would cut runner minutes and get `.NET` results back sooner.

- **Tradeoffs:** not on the critical path, so it saves no wall-clock today. It matters if #1–#3 ever
  bring e2e under ~7 min. Watch the Hosting suite's inotify use if test assemblies run in parallel.

### 8. Don't gate `ci` on the merged HTML report: −0.5 min

`ci` needs the whole `e2e` reusable workflow, including `report`, which only merges blobs into HTML
(about 25s plus scheduling). Having `ci` depend on the shards' outcome and leaving `report` to run
alongside would save ~30s.

- **Tradeoffs:** a reusable workflow exposes a single `result`, so this needs either the merge folded
  into the gate job or the shard results exported as outputs. It's small, so do it only if the
  restructure for #1 makes it cheap.

### Considered, not recommended

- **More caching (bundle by content hash, `obj/` caches).** Branch-scoped cache isolation means a PR's
  bundle cache only helps re-runs of that PR, and caching incremental `obj/` state risks stale-build
  results. Dependency caches already hit.
- **Skipping jobs by changed paths.** In the last 40 merged PRs: 17 web-only, 8 .NET-only, 14 both,
  1 docs-only. Web-only PRs still need e2e and native builds (they embed `wwwroot`). .NET-only PRs still
  need the full stack. Only `.NET tests` (web-only) and `web checks` (.NET-only) could be skipped,
  neither on the critical path, so there's no wall-clock win in exchange for the risk of skipping a
  real dependency.
- **Larger (8/16-core) runners for the bundle.** These are billed even on a public repo. The AOT compiles
  are sequential (see #2), so fix that first; reconsider only if a parallel bundle is still >3 min.
- **Retries, quarantines, or dropping slow tests.** Excluded by policy, and the slow tests here (render
  budgets, 300k-line review) guard real regressions.

## Projected critical path

| After | Bundle | Slowest shard | Report + gate | Total p50 |
|---|---|---|---|---|
| Today | 255s | 590s | 35s | ~15.3 min |
| #1 balanced, 6 shards | 255s | ~470s | 35s | ~13.3 min |
| + #2 parallel bundle | ~180s | ~470s | 35s | ~12.0 min |
| + #3 8 shards | ~180s | ~365s | 35s | ~10.3 min |
| + #5 macOS format split | — | — | — | p90 stays e2e-bound |
| + #4 −0.5s boot | ~180s | ~330s | 35s | ~9.7 min |
