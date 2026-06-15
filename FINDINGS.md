# weavie First Prototype — Build Findings

> Autonomous overnight build of the weavie First Prototype (a latency-and-feel gate that
> wraps the real interactive Claude Code TUI). Branch: `overnight/first-prototype`.
> This log is honest: a fail or partial is a valid finding. Updated incrementally.

Started: 2026-06-15 (overnight). Machine: Apple Silicon, macOS 25.3.0, 120Hz display.
Toolchain verified: dotnet 10.0.301, node v25.6.0, npm 11.8.0, claude 2.1.177, Xcode + `macos` workload, `ANTHROPIC_API_KEY` UNSET (correct — interactive billing).

---

## Status at a glance

| Step | Item | Status |
|---|---|---|
| 0 | Branch, build gates, CPM, interface seams, example-flow T1 test | ✅ Done |
| 1 | Monaco "just type" + rigorous keypress→paint latency harness | ✅ Done |
| 2 | xterm.js + WebGL + PTY → interactive `claude`; verify subscription billing | ⏳ Pending |
| 3 | IDE-MCP `openDiff` (sole edit feed) — real handshake, render to Monaco | ⏳ Pending |
| 4 | Clickable `file:line` (OSC 8 + regex link provider → reveal in Monaco) | ⏳ Pending |
| 5 | Side-by-side two-pane Solid chrome | ⏳ Pending |

---

## Step 0 — Foundation ✅

**Done.** Whole solution builds with **0 warnings**; **25 T1 tests green**.

- **Branch:** `overnight/first-prototype`.
- **Build gates** (`Directory.Build.props`, applies to all projects): `Nullable=enable`,
  `TreatWarningsAsErrors=true`, `EnableNETAnalyzers=true`, `AnalysisLevel=latest`,
  `EnforceCodeStyleInBuild=true`, `ImplicitUsings=enable`, `LangVersion=latest`.
  *Verified the gate bites:* it failed the build on a `CA1422` (obsolete `ActivateIgnoringOtherApps`)
  in the throwaway spike — exactly the behavior we want.
- **Central Package Management** (`Directory.Packages.props`): pinned xunit 2.9.3,
  xunit.runner.visualstudio 3.0.2, Microsoft.NET.Test.Sdk 17.12.0.
- **Interface seams** (the vault Build-Philosophy testability seams; real impl + in-memory fake, no fallbacks):
  - `IFileSystem` → `LocalFileSystem` (real) / `InMemoryFileSystem` (test fake).
  - `IDocumentModel` → `InMemoryDocumentModel` (T1 fake over a tiny `TextBuffer` with Monaco
    line/column semantics). Prod Monaco-proxy impl arrives in step 1/3.
  - `IDocumentModelFactory` → `InMemoryDocumentModelFactory`.
  - Edit feed modeled as `DiffProposal` (shaped exactly like MCP `openDiff`:
    old_file_path / new_file_path / new_file_contents / tab_name) resolved by a `DiffSession`
    (blocking Keep/Reject → `DiffOutcome` mapping to FILE_SAVED / DIFF_REJECTED).
- **Example-flow T1 test (the spine), green first:** openDiff-shaped edit → document model →
  user types into the proposed diff → Keep → save → assert in-memory FS has the saved content.
  Plus Reject-leaves-FS-untouched, new-file creation, double-resolve guard, and range-math unit tests.

**Decision (logged):** `AnalysisLevel=latest` with the *default* analysis mode (not `All`) — the
quality bar specifies `latest`; `All` would add high-friction style churn overnight for little gain.
Code-style enforcement (`EnforceCodeStyleInBuild`) is on globally and the generated AppKit bindings
pass it.

---

## Step 1 — Monaco "just type" + latency harness ✅

**Done.** Real Monaco renders in the real WKWebView and is typable; a rigorous keypress→paint
harness is wired and produces objective numbers.

- **Stack stood up:** Solid + Vite + TS web app (strict `tsc --noEmit` clean, Biome clean),
  Monaco editor (TS sample, bracket-pair colorization, minimap), workers wired via Vite
  (classic/iife so they load under the custom scheme).
- **Production Mac shell (replaced the storyboard spike):** programmatic `AppDelegate` →
  `NSWindow` + `WKWebView`; the web app is served from the bundle via a custom **`app://`
  scheme handler** (`AppSchemeHandler`, path-traversal-guarded, correct MIME types) — no
  network, no localhost port, secure same-origin (so workers + Event Timing API behave).
- **Bridge proven end-to-end** (`HostBridge`): JS→C# `postMessage` and C#→JS
  `__weavieReceive` round-trip (`ready → monaco-ready → latency-live → benchmark-result`).
- **The harness** (`src/web/src/latency/`): two complementary live signals for real
  keystrokes — fine-grained **keydown→frame** (rAF) and the browser's **input→paint**
  (Event Timing API) + per-keystroke handler cost + frame-interval/Hz; plus a reproducible
  **synthetic benchmark** (edit-apply→frame) over idle and under-load (a `LoadGenerator`
  burns 5ms/frame to simulate the coming terminal firehose). Hot-path discipline: the
  keydown handler only stamps a timestamp + schedules one rAF; percentile math + HUD/host
  updates are deferred to a 500ms timer — never in the keystroke path.
- **Screenshot:** `docs/screenshots/step1-latency.png` (captured via `WKWebView.TakeSnapshot`,
  which works even when the window is occluded).

## Latency numbers

**Headless run, screen awake, ProMotion idled to 60Hz (`displayHz` reported 59; frame ≈16.7ms):**

| phase | edit→frame p50 | p95 | p99 | max | note |
|---|---|---|---|---|---|
| idle | **13 ms** | 17 | 18 | 1182* | *one cold-start outlier (now fixed via warmup) |
| under-load | **10 ms** | 11 | 12 | 12 | tail stays tight under simulated firehose |

These are **synthetic edit→frame (render latency)** at 60Hz — the dominant controllable
component. True keydown→paint for the *feel* verdict is the live Event-Timing meter, which
needs a human typing (the goal explicitly leaves the feel verdict to the user). The earlier
R15 gut-check on the awake 120Hz panel was keydown→frame p50 ~7–10ms / p95 ~17–20ms,
consistent with these (a 120Hz panel roughly halves the frame-quantized component).

**Not catastrophic — nowhere near a hard-stop.** Re-run awake & frontmost on the 120Hz panel
for the real-feel numbers: launch the app, type, read the HUD (p50/p95/p99/max, live), and
click "run benchmark".

**Two toolchain findings worth keeping (see memory):**
1. **Bash sandbox throttles I/O-heavy builds ~90×.** A Vite build of Monaco (thousands of
   small module files) took **15m37s at 1% CPU** sandboxed vs **9.9s at 140% CPU**
   sandbox-disabled — identical 14s user time, pure syscall throttling. Run npm/vite/
   dotnet-macos builds with the sandbox disabled.
2. **Display state corrupts unattended GUI measurement.** When the screen is *locked* (not
   just asleep), the WKWebView window is occluded → the page is hidden → `requestAnimationFrame`
   pauses and `setTimeout`/`setInterval` clamp to ~1/s. The benchmark used to hang on a dead
   rAF; it now races rAF against a 200ms timeout, excludes non-rendered samples, and
   **honestly flags `framesLookThrottled`** instead of reporting garbage. Real numbers need
   the screen awake (run 1 above was captured before the lock). `WKWebView.TakeSnapshot`
   still renders occluded content, so screenshots work regardless.

## Billing method + evidence

_(lands in step 2)_

## MCP handshake — what was reverse-engineered

_(lands in step 3)_

## Prioritized next steps

_(lands at the end)_
