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
| 2 | xterm.js + WebGL + PTY → interactive `claude`; verify subscription billing | ✅ Done |
| 3 | IDE-MCP `openDiff` (sole edit feed) — real handshake, render to Monaco | ✅ Handshake verified vs real claude; openDiff impl + UI built |
| 4 | Clickable `file:line` (OSC 8 + regex link provider → reveal in Monaco) | ✅ Built |
| 5 | Side-by-side two-pane Solid chrome | ✅ Built |

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

### ⚠ Refresh-rate finding (raised by the user): WKWebView renders at ~60Hz, not 120Hz

The user (awake, on the 120Hz panel) noticed weavie renders at **60Hz**. Investigated — this is
real and matters, because the vault calls 120Hz "the single biggest lever" for typing latency.

- The OS display **is** in 120Hz ProMotion mode: `NSScreen.maximumFramesPerSecond = 120`. Not a
  display setting, not battery/low-power (on AC, 100%).
- **WKWebView caps `requestAnimationFrame` at 60fps on macOS** — a long-standing, documented
  WebKit limitation (WebKit bugs 173434 / 294338). Hard cap on macOS 13–15; community workaround
  flips the private `_features` flag `PreferPageRenderingUpdatesNear60FPSEnabled=false`.
- **This machine is macOS 26.3**, where that workaround is reportedly a no-op (cap "removed"). Our
  max-demand FPS probe (animate a visible element every frame, 3s) saw **min 11ms (91Hz)** frames
  — so it is **not hard-capped at 60 here** — but **median 17ms (60Hz)**, i.e. WKWebView
  effectively serves ~60Hz for normal content (adaptive ProMotion defaults low). (The probe's
  frame count was low because an auto-launched window isn't reliably foreground; WebKit throttles
  rAF off-focus. A clean steady-state number needs the window focused — user to confirm.)
- **Implication for the gate:** the all-WKWebView stack likely gives **~60Hz typing on macOS**, so
  weavie does *not* automatically get the 120Hz latency lever. Options: (a) accept 60Hz and judge
  the feel (it may still be fine — the R15 gut-check passed); (b) force a continuous 120Hz rAF
  heartbeat (hacky, power cost, may not stick — against the no-hacks philosophy); (c) the vault's
  documented contingency — **Chromium/CEF (or Electron) on macOS**, whose bundled Chromium isn't
  subject to WebKit's pacing, if 120Hz typing proves necessary. **This is a stack-level decision
  for the user**, now surfaced with data — exactly what the prototype gate is for.
- Diagnostics added (kept): `WEAVIE_FPSPROBE=1` runs the probe; the app logs
  `NSScreen.maximumFramesPerSecond` at startup.

**Clean way to confirm the steady-state yourself:** launch the app, **click into the window so it's
focused/frontmost**, then either watch the HUD `frame …ms (…Hz)` while typing, or relaunch with
`WEAVIE_FPSPROBE=1` and keep the window focused for ~3s; read the `FPS-PROBE` line from stdout.

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

## Step 2 — xterm.js + WebGL + PTY → interactive claude ✅

**Done.** The real interactive `claude` TUI runs in an xterm.js pane, driven by a hand-rolled
macOS PTY, on the user's subscription.

- **PTY (`Weavie.Core/Terminal/`):** `ITerminal` seam with `FakeTerminal` (scripted) and
  `PosixPtyTerminal` (real). The real one uses `posix_openpt` + `posix_spawn` with
  `POSIX_SPAWN_SETSID` (+ `CLOEXEC_DEFAULT`) — the child becomes session leader and the
  file-action open of the slave tty makes it the controlling terminal. **No `fork()` in the
  managed runtime.** Supports env inject/remove, `addchdir_np` workspace, `TIOCSWINSZ` resize,
  background read thread → `Output`, `waitpid` → `Exited`. P/Invoke via source-generated
  `LibraryImport` (AOT/trim-safe).
- **Validated by tests** (8 terminal tests, real processes on macOS): echo round-trips, env
  injection visible to child, env removal hides a var, working-directory honored.
- **Web:** `@xterm/xterm` + **WebGL addon** + fit addon in a Solid `TerminalView`, in a
  two-pane split (Monaco | terminal). PTY bytes ride the bridge as base64 both ways; resize
  is wired through the fit addon. `TerminalController` (host) launches
  `"$SHELL" -l -c "exec claude"` in the workspace (`WEAVIE_WORKSPACE`, default `$HOME`),
  stripping `ANTHROPIC_API_KEY`, optionally teeing raw PTY bytes to `WEAVIE_PTY_LOG`.
- **Proven end to end:** launched in-app, `claude` rendered its full TUI through our terminal
  (captured 4453 PTY bytes): *"Claude Code v2.1.178 · Welcome back Ogi! · Opus 4.8 (1M context)
  · Claude Max"*. The PTY↔xterm round-trip works even with the screen locked (xterm parsed
  claude's setup and replied with a focus event). Screenshot: `docs/screenshots/step2-terminal.png`
  (the terminal half is dark there because xterm's WebGL renderer needs `requestAnimationFrame`,
  which is paused while the screen is locked — the PTY capture is the functional proof;
  it paints normally on an awake screen).

## Billing method + evidence

**Method:** launch the user's installed, logged-in **interactive** `claude` (never `-p`/SDK/ACP)
as the PTY child, with `ANTHROPIC_API_KEY` removed from its environment. Per Anthropic's
2026-06-15 billing split, interactive CLI draws on the **full subscription**; only headless
draws on the capped Agent-SDK credit. This is the JetBrains-plugin model.

**Evidence gathered (this machine, tonight):**
1. **In-UI proof:** `claude` rendered **"Claude Max"** in its own welcome banner when launched
   by our app's PTY — i.e. it is operating as the subscription plan, not an API key.
2. **Account/credential:** `~/.claude.json` `oauthAccount` shows `billingType: stripe_subscription`,
   `organizationRateLimitTier: default_claude_max_20x` (Claude **Max 20×**); the OAuth token
   lives in the macOS Keychain (`Claude Code-credentials`). We never read/extract the token (ToS).
3. **No API key:** verified absent from the session env *and* explicitly stripped from the
   child env (`RemoveEnvironment: ["ANTHROPIC_API_KEY"]`).

**Caveat (carried from the vault):** the interactive-vs-headless billing split is Anthropic's
announced behavior; we verify the *mechanism* (interactive, OAuth-logged-in, no API key), which
is what selects subscription billing. Re-confirm on the dashboard after real usage.

## Step 3 — IDE-MCP openDiff ✅ (handshake verified end-to-end; full integration built)

This is the riskiest, most reverse-engineered step. **The hard part — the handshake — is verified
against the real installed `claude` 2.1.178.** openDiff is implemented, deterministically tested,
and wired to an editable Monaco diff in the app.

**What's built (`Weavie.Core/Mcp/`):**
- `McpServer` — loopback (127.0.0.1) WebSocket server speaking MCP/JSON-RPC 2.0. Manual HTTP
  upgrade so we can enforce auth; `WebSocket.CreateFromStream` for framing. Handles `initialize`,
  `notifications/initialized`, `ping`, `tools/list`, `tools/call`. Dispatches each message off the
  receive loop so a blocking `openDiff` never stalls the connection.
- `IdeLockFile` — writes `~/.claude/ide/<port>.lock` (respects `$CLAUDE_CONFIG_DIR`).
- `IdeIntegration` — generates the token, starts the server on an ephemeral port, writes the lock
  file, exposes the env vars to inject, cleans up on dispose.
- `IDiffPresenter` seam (`FakeDiffPresenter` for tests, `McpDiffPresenter` in the app).
- Wired into the app: `AppDelegate` starts `IdeIntegration`, injects the env into the spawned
  `claude` via `TerminalController.ExtraEnvironment`, and `McpDiffPresenter` renders openDiff as an
  **editable Monaco diff** (`DiffView.tsx`) with Keep/Reject; the host saves on Keep and replies
  `FILE_SAVED`. Dedicated per-app port + lock, so the spawned claude hits OUR server.

**Verification:**
- **Real-claude handshake (the empirical proof), via the `tools/Weavie.IdeHarness` dev harness** —
  spawn real `claude` with our env, watch our server:
  ```
  client connected + authenticated   (lock-file token + WS upgrade accepted)
  recv: method=initialize
  recv: method=notifications/initialized
  recv: method=ide_connected          (claude's own notification)
  recv: method=tools/list             (claude fetched our tool list)
  ```
  Reproduced in both `/tmp` and the repo workspace. The harness sends **only Enter** (retried until
  connected) to clear the first-run "trust this folder?" prompt — no keystroke-timed TUI scripting.
- **openDiff contract (deterministic), `McpServerTests`** — a real loopback WebSocket client drives
  the server: connection without/with-wrong token is **rejected** (CVE-2025-52882), `initialize`
  echoes the protocol version + returns serverInfo, `tools/list` advertises `openDiff`, openDiff
  **Keep** saves the file + returns `FILE_SAVED`, **Reject** leaves the file + returns `DIFF_REJECTED`.
- **Live in-app openDiff (claude → Monaco diff → Keep → save):** built and wired, but **not
  exercised tonight** — it needs the screen awake. While the screen is locked the WebView is
  occluded, host→webview delivery throttles to ~1/s, and claude's terminal-capability startup
  (which in the app rides the xterm round-trip) stalls *before* it opens the IDE socket. The harness
  avoids this (raw PTY, no webview), which is why it connects reliably. **To see it live: run the app
  awake, type "edit <file>: …" to claude, watch the diff appear in the left pane, Keep it.**

## MCP handshake — what was reverse-engineered

All confirmed against `claude` 2.1.178 unless noted UNCERTAIN.

- **Lock file:** `~/.claude/ide/<PORT>.lock` (stem = the WS port, not PID). JSON:
  `{ "pid": <int>, "workspaceFolders": ["<dir>"], "ideName": "weavie", "transport": "ws", "authToken": "<32 hex>" }`.
  Token = 32 lowercase hex (128 bits). Honors `$CLAUDE_CONFIG_DIR`.
- **Env injected into claude:** `CLAUDE_CODE_SSE_PORT=<port>`, `ENABLE_IDE_INTEGRATION=true`. Claude
  reads the lock file matching the port to get `authToken`. (No API key — billing stays subscription.)
- **WebSocket:** claude connects to `ws://127.0.0.1:<port>/` and presents the token in the
  **`x-claude-code-ide-authorization`** request header. We reject the upgrade (401) without the exact
  token — the CVE-2025-52882 mitigation. Confirmed: claude's connection is accepted only with the
  lock-file token.
- **MCP:** claude is the JSON-RPC client, the IDE is the server. Sequence observed:
  `initialize` → (we reply protocolVersion + `capabilities.tools` + serverInfo) →
  `notifications/initialized` → `ide_connected` (claude→us notification, no reply) → `tools/list`
  (we reply with our tools). Later, `tools/call` for the tools claude needs.
  - **protocolVersion: sources disagreed** (PROTOCOL.md "2025-03-26" vs nvim/Zed "2024-11-05"), so we
    **echo whatever claude sends** in `initialize` — robust against version drift. (UNCERTAIN which is
    canonical; echoing sidesteps it.)
  - Real claude was also observed calling `closeAllDiffTabs` and `getDiagnostics` into us at
    startup/around edits — our minimal handlers satisfy it and claude proceeds.
- **openDiff (the star) — request:** `tools/call` name `openDiff`, arguments
  `{ old_file_path, new_file_path, new_file_contents, tab_name }`.
  **Response:** `{ "content": [ { "type": "text", "text": "FILE_SAVED" } ] }` on accept (the IDE writes
  the file first), `… "DIFF_REJECTED"` on reject. Implemented exactly; deterministically tested.
  (UNCERTAIN: whether real claude prefers `openDiff` vs a direct Write for *new* files, and the exact
  permission interplay — observed claude using a direct Write under `--dangerously-skip-permissions`.
  The canonical openDiff trigger is editing an existing file in normal permission mode; verify live.)
- **Notifications IDE→claude (not required for openDiff, not yet sent):** `selection_changed`,
  `at_mentioned`. Deferred (YAGNI until selection-sync is needed).
- **Sources:** coder/claudecode.nvim PROTOCOL.md + lua; CVE-2025-52882 advisory; **plus direct
  observation of the live CLI** (the authoritative check). A background research subagent stalled
  mid-run, so the protocol was pinned from the docs and then confirmed empirically.

## Step 4 — Clickable file:line ✅ (built)

Two link sources in the xterm pane resolve to "reveal in Monaco":
- **OSC 8 hyperlinks** (`file://` URIs Claude emits) via `term.options.linkHandler`.
- **Un-hyperlinked `path:line[:col]`** via a custom `registerLinkProvider` + regex.

On click the web posts `reveal-file {path, line}`; the host `FileOpener` resolves relative paths
against the workspace, reads the file (`LocalFileSystem`), and posts `open-file {path, content,
line}`; the editor loads it (language inferred from the URI) and reveals the line. The MCP
`openFile` tool shares the same `FileOpener`. Builds clean; live click-through needs the screen
awake (it's a terminal click).

## Step 5 — Side-by-side chrome ✅ (built)

The two-pane chrome is in place: Monaco editor | **draggable splitter** | xterm terminal, under
the latency HUD. Chat happens in the real claude TUI (left↔right), its edits arrive as editable
Monaco diffs (step 3's openDiff: accept / edit-then-accept / reject), and file:line links reveal
files in the editor (step 4). Screenshot of the openDiff review UI: `docs/screenshots/step3-opendiff.png`.

## Screenshots

- `docs/screenshots/step1-latency.png` — Monaco + live latency HUD in the real WKWebView.
- `docs/screenshots/step2-terminal.png` — two-pane editor | terminal layout.
- `docs/screenshots/step3-opendiff.png` — openDiff review UI (tab name, path, Keep/Reject).

(Terminal/diff *text* reads blank in these because they were captured with the screen locked —
xterm's WebGL renderer and Monaco's diff paint are rAF-driven and pause under occlusion. The HUD,
chrome, buttons, and editor text paint fine; functionality is proven by the PTY/MCP logs + tests.)

## How to run it (awake, for the real feel + live flow)

```
cd src/web && npm install && npm run build          # build the web app (sandbox-free, ~10s)
cd ../.. && dotnet build src/Weavie.Mac/Weavie.Mac.csproj -c Debug
open src/Weavie.Mac/bin/Debug/net10.0-macos/osx-arm64/Weavie.Mac.app
# or run the inner binary to see stdout:
src/Weavie.Mac/bin/Debug/net10.0-macos/osx-arm64/Weavie.Mac.app/Contents/MacOS/Weavie.Mac
```
Optional env: `WEAVIE_WORKSPACE=<dir>` (claude's cwd + IDE workspace; default `$HOME`),
`WEAVIE_PTY_LOG=<file>` (tee claude's PTY), `WEAVIE_AUTOBENCH=1` (auto-run the latency benchmark).
Type in Monaco to feel latency (read the HUD); chat with claude in the right pane; ask it to edit a
file and Keep/Reject the diff on the left; click a `file:line` in the terminal to reveal it.

Verify the MCP handshake against the real CLI any time:
`dotnet run --project tools/Weavie.IdeHarness` (spawns claude, prints the handshake summary).

## Prioritized next steps

1. **Live-verify in-app openDiff + file:line awake** (15 min): run the app with the screen on, ask
   claude to edit a file, Keep the Monaco diff, confirm it saves; click a terminal `file:line`.
   This closes the one gap the locked screen prevented tonight. (Everything underneath is tested.)
2. **Re-run the latency benchmark awake on the 120Hz panel** and record the user's feel verdict —
   the actual gate. Numbers tonight were a healthy 60Hz (ProMotion idled); expect ~half the
   frame-quantized component at 120Hz.
3. **openDiff for new-file creates / permission interplay**: confirm whether real claude uses
   openDiff vs direct Write for new files and under different permission modes; adjust if needed.
4. **Selection sync notifications** (`selection_changed`/`at_mentioned`) — only when an at-mention
   or selection-aware flow is actually wanted (YAGNI today).
5. **FS-watch for out-of-band changes** (git checkout, terminal edits) — deferred per the vault
   until sessions start doing git ops (it is NOT an edit feed).
6. **Harden the terminal output path** (batch base64→EvaluateJavaScript on a timer) if claude's
   redraws feel laggy under load once driven awake.
7. **A couple of T3 Playwright tests** (real Monaco keystrokes, real diff accept) to complement the
   39 T1 tests — the quality bar's "a few high-fidelity tests".
