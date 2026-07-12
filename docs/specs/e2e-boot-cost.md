# E2E boot-cost reduction

Status: implemented (Item B shipped; Item A abandoned at its gate)
Last updated: 2026-07-04

## Outcome (what implementation found)

Instrumenting the boot with the `?startuptiming` marks corrected the plan's central premise and
resized both items:

- **The splash was already dropped on the first terminal paint, not on editor-ready** (App wires
  `dismissSplash` to `TerminalView.onFirstRender`; the stale `main.tsx` comment said otherwise). The
  splash *element* is removed at ~180ms (1x) / ~780ms (4x throttle). But the user didn't SEE the
  reveal until ~editor-ready, because `editor.start()` ran synchronously in `onMount` and the
  ~6.9MB editor chunk's eval + Monaco creation **jammed the renderer main thread**, starving the
  reveal paint. So the real fix is not "move the splash gate" (already moved) — it is **defer the
  editor bring-up until one frame past the first terminal paint** so the shell reveals first.
  - Measured reveal (dom→splash-observed): **1x 902ms → 180ms; 4x throttle 2762ms → 430ms** (~2.3s
    on CI-like CPU). The editor still comes up right after (editor-ready unchanged at ~1.1s/~3.4s).
  - Shipped as Item B below. Half the suite (non-editor journeys) proceeds ~2s earlier on throttled
    CI; editor journeys are unchanged (their helpers now wait on a new `data-ready` signal).
  - Liveness: the terminal paint is the reveal trigger, but a launch with zero loaded terminals (an
    all-dormant restore, an offline remote backend) never paints one — so the editor also comes up
    once the host's session state has arrived with no active-backend terminal (and on becoming
    visible, for a window occluded at launch). That path reveals at editor-ready, exactly as before.
  - **Local unthrottled suite wall-time was unchanged (37.3s vs 37.4s at 50% workers on 24 cores)** —
    honest: with spare cores the eval was already hidden. The win is real only under CPU contention
    (CI 4-vCPU, serial macOS/Windows) and as the product reveal-latency win. CI is the true measure.

- **Item A (V8 code-cache reuse) failed its go/no-go gate: ~0ms.** Measured its actual mechanism
  cleanly — a persistent context + fixed port (same origin ⇒ warm code cache) across three
  sequential *fresh* hosts — saving was splash **-43ms**, editor-ready **+17ms** (noise); editor-ready
  sat at ~3.4s cold *and* warm under 4x throttle. The editor bring-up is **execution-bound** (Monaco
  builds DOM, vscode services initialize), not compile-bound, so caching compiled bytecode buys
  nothing. An earlier reload-in-same-page proxy showed a false ~1.3s "saving" that was entirely
  host-session-state reuse, not the code cache. **Abandoned; not built** — the persistent-context /
  stable-port / hand-rolled-tracing / storage-isolation machinery isn't worth ~0ms plus isolation
  risk. This is exactly the number the measure-first gate existed to find.

## Context: where a test's fixed cost goes (measured)

Every functional e2e test boots the full stack. Phase timings, measured on a fast 24-core dev box
(uncontended) and re-measured under a 4× renderer-CPU throttle to approximate a 4-vCPU CI runner:

| phase | fast box | 4× throttled |
|---|---|---|
| temp HOME + git workspace scaffold | 25ms | ~same (I/O) |
| dotnet host spawn → listening | 390ms | ~1s |
| navigate → DOM ready | 105ms | 230ms |
| DOM ready → app interactive (splash gone) | **~950ms** | **~2,200ms** |
| fake-claude spawn (inside session-ready) | 35ms | negligible |
| teardown (post PR #234) | ~5ms | ~same |

The dominant cost is **renderer CPU evaluating the ~6.9MB `vscode-services` chunk + Monaco workbench
init, paid per test** — each fresh Playwright context is a fresh V8 with no code cache, and the
OS-assigned per-test port makes every bundle URL unique, so nothing can cache. Download itself is
66ms on loopback; it is not bandwidth.

**Ruled out — do not chase these** (all measured):

- Debug vs Release host: identical boot (ASP.NET's framework is precompiled; Weavie's assemblies are
  small). Release additionally requires the NativeAOT relay toolchain (`src/HookRelay.targets`).
- fake-claude CLR start: 35ms.
- .NET AOT/R2R anything: the .NET side is ~400ms of a ~1.4s fixed cost.
- Electron-style V8 heap snapshots: not available on OS webviews (WebView2/WKWebView), and not
  applicable to the Playwright-driven Chromium either — the code cache (item A) is the platform's
  mechanism.
- Playwright tracing costs ~21% of e2e wall (measured 14.9s vs 11.7s for 6 serial tests). Deliberately
  kept: retries are off by policy (a flake must fail the run), so the always-on trace is the only
  forensics for a first failure — it is what root-caused the S3 Windows layout collapse.

CI medians after PR #234: Linux 5.0s (2 workers), macOS 3.6s, Windows 2.4s (serial). The two items
below attack the ~1-2.2s browser-side share. They are independent; land as separate PRs.

## Item A — V8 code-cache reuse within a worker (harness-only, measure-first)

> **ABANDONED at the gate (~0ms saving).** See Outcome above. The plan below was not built; kept as
> the record of what was evaluated.

**Idea:** one persistent browser context per Playwright worker + one stable port per worker, so the
bundle URL (the V8 code-cache key) is constant within a worker. Chromium writes the compile cache
into the profile on an early load and skips parse/compile on subsequent loads; tests ~3..N per worker
stop paying compile (execution still runs).

**Gate:** this is a prototype with a go/no-go number. If the steady-state per-test saving on a
throttled run is under ~300ms, abandon it — the isolation trade isn't free.

Steps:

1. **Stable per-worker port.** In `src/web/e2e/harness/weavie-host.ts`, take the port as a parameter:
   `44100 + workerInfo.parallelIndex` (collision-free across workers by construction). Pass
   `WEAVIE_SERVE_PORT` accordingly instead of `0`. A bind failure must stay a loud failure — the host
   exits and `waitForListening` already rejects with its log; no retry/fallback (repo rule). Notes:
   a LISTEN socket does not enter TIME_WAIT, so rebinding the same port right after
   `killProcessTree` is fine on Linux/macOS and on Windows (taskkill closes the listener); verify
   this holds by running one worker's specs serially in a loop locally.
2. **Persistent context per worker.** In `src/web/e2e/harness/fixtures.ts`, add a worker-scoped
   fixture that calls `chromium.launchPersistentContext(userDataDir, ...)` with a per-worker
   `mkdtemp` profile dir and the project's `use` options (viewport). Override the built-in `page`
   fixture to create a fresh page from that context per test (and close it after).
3. **Hand-rolled tracing.** Overriding `context` bypasses Playwright's built-in tracing fixture —
   the config's `trace: "retain-on-failure"` will silently stop applying. Reimplement it in the
   fixture: `context.tracing.start({ screenshots: true, snapshots: true, sources: true })` once per
   worker, `tracing.startChunk()` before each test, `stopChunk({ path })` after, attach the file
   only when `testInfo.status !== testInfo.expectedStatus`. Verify a forced failure still yields a
   working trace.zip AND the existing attachments (weavie-host.log, fake-claude.log,
   viewport-layout.json — see the failure block in fixtures.ts).
4. **Storage isolation audit.** The origin is now stable within a worker, so web storage persists
   across tests. Audit `grep -rn "localStorage\|sessionStorage\|indexedDB" src/web/src` — anything
   the app persists per-origin leaks between tests. Clear origin storage in the per-test page
   teardown (cookies via `context.clearCookies()`, storage via a `page.evaluate` on the old page
   before close). If the app turns out to keep meaningful boot state in localStorage, reconsider —
   correctness beats speed.
5. **Measure.** Run a full `--project=headless` pass with 1 worker, before vs after, plus a 4×-throttled
   probe (CDP `Emulation.setCPUThrottlingRate` — throttle must be applied per-page, after boot,
   reload to measure). Compare medians and the DOM-ready→splash-gone phase specifically. Apply the
   gate. Report numbers in the PR.

Risks: cross-test bleed through the shared profile (mitigated in step 4; each test still gets a
fresh page, fresh host process, fresh HOME/workspace); the `remote` project must keep working — its
worker URL comes from the runner, so scope the stable-port scheme to `launchHeadless` and leave
`launchRemote` on its current path (its per-test cost is dominated by runner+worker spawn anyway).

## Item B — defer the editor bring-up past first paint (product change) — SHIPPED

> **SHIPPED.** The splash was already terminal-gated; the real fix was deferring `editor.start()` one
> frame past the first terminal paint so the editor eval stops starving the reveal (see Outcome
> above). Editor-touching e2e helpers now wait on a `data-ready` signal on `.editor`.

**Today:** `src/web/src/main.tsx` already renders the shell immediately and loads Monaco + vscode
services as a separate lazy chunk — but `src/web/src/App.tsx` (see the `editor.start(editorContainer)`
call in `onMount`, ~line 690) holds the splash until that chunk settles. So both users and tests pay
the full multi-MB eval before "interactive", even though the infrastructure to defer it already
exists. On a throttled CPU that is ~2.2s of splash for work the first paint doesn't need.

**Target:** splash drops at shell-interactive (layout + first session ready); the editor pane keeps
an honest loading placeholder until its chunk resolves. Users get a usable terminal/palette ~1-2s
earlier on slow machines; roughly half the e2e suite (terminal, session-lifecycle, palette, layout,
media, approval/hook journeys) stops paying the editor eval entirely, and editor tests overlap it
with their earlier steps.

Steps:

1. **Message-buffering audit first.** With the splash no longer serializing "editor exists" before
   user actions, host pushes routed to the editor (`editor.handleMessage` in App.tsx — openDiff,
   turn-changes, open-file) can arrive before the chunk resolves. Read the editor controller
   (`src/web/src/editor/`) and establish what happens today: messages must be queued until ready, or
   re-requested on ready. If neither, implement queueing in the controller — this is the actual work
   of this item, and skipping it converts a boot-latency win into a race factory.
2. **Move the splash gate.** Dismiss on shell-mounted + first-session-ready instead of editor-settled
   (`src/web/src/splash.ts`, App.tsx). The editor pane placeholder already exists ("its pane shows a
   placeholder until it resolves").
3. **Expose a deterministic editor-ready signal** — e.g. a `data-ready` attribute on the `.editor`
   container the moment the chunk's controller is live (an attribute flip, not a new event system).
4. **Gate the harness's editor helpers, not every spec.** `src/web/e2e/harness/actions.ts`
   (`openFile`) and `harness/navigator.ts` (`walkToChangedFile`, `focusEditor`) wait on the
   editor-ready signal before interacting. The fixture's splash wait stays as-is — it just fires
   earlier now. Individual specs should not need edits; any spec that pokes `.monaco-editor`
   directly without going through the helpers needs routing through them (grep for direct usage).
5. **Verify like a product change, not a harness change.** Full suite on all three OSes via the PR
   (three consecutive green runs — this alters boot ordering, the exact area of the historical
   Windows/macOS regressions); `weavie-reviewer` on the diff; in a remote sandbox also `weavie-tester`
   exercising: boot → type in claude pane before editor ready → open file → editor appears; and a
   PR-open journey (the S3 family) — session switches while the editor chunk is mid-load are the
   riskiest path.
6. **Measure and report**: per-OS medians before/after from the CI list-reporter durations, plus the
   local DOM-ready→splash-gone number.

Non-goals for both items: retries (policy: a flake fails the run), trimming the OS matrix (the full
matrix is deliberate — it catches OS-specific app breakage, e.g. the S3 Windows layout collapse),
Release/AOT builds for e2e (measured: no gain), touching the 30s expect ceilings (revisit only after
the S3 root cause is fixed).
