# Architecture review — July 2026

A full-codebase review of Weavie's architecture: Core/Hosting layering, the bridge protocol, the
process/PTY/hook-bridge layer, the web frontend, the remote story, and the test architecture.
Findings are ranked by severity with file references; recommendations are ordered as a roadmap.
Line numbers reference the tree at the time of review and will drift.

## Verdict in one paragraph

The macro-architecture is right and holding: rendering in the web layer, compute in a host, one
bridge between them, one `HostCore` behind every shell, one supervisor for every long-lived child.
The dependency graph is clean and enforced by project references, session isolation is principled,
and the remote security model (default-deny middleware, `ListenMode` making
exposed-without-auth unrepresentable, constant-time token compares) is genuinely good. The
problems are concentrated at two places: **the bridge has no contract** (three independent copies
of the message shapes, silent drift, per-feature correlation with inconsistent failure policy), and
**the central seams violate the project's own standards** (nullable injected deps and a mode flag in
`McpServer`, silent-empty fallbacks, god files well past the 300-line rule). Plus one operational
hole: the remote-security test suites exist but never run in CI.

## What is sound (preserve these)

- **Layering**: `Weavie.Core` references nothing, `Weavie.Hosting` references only Core, all four
  shells reference both. No Hosting types leak into Core; Core's web-protocol builders take an
  `Action<string> postToWeb` delegate rather than the bridge interface.
- **Composition**: no DI container; each shell is a small hand-wired root over
  `HostServices.CreateDefault()` with `required init` members, `with`-based fake substitution in
  Headless, and one `TestHost` factory driving the real `HostCore` over a fake bridge.
- **Session isolation invariants**: `fs-*` routed by path to the owning session
  (`HostCore.WebBridge.cs`), editor messages stamped with the owning session id and rejected when
  stale, terminal frames double-tagged `(slot, session)`, active-backend gating of page paints.
- **Supervisor compliance is 100%**: every long-lived child (claude PTY, shell PTY, LSP servers,
  Vite dev server, headless worker) runs under `ProcessSupervisor`; every raw `Process.Start` in
  the tree is genuinely one-shot. The PTY layer is unusually careful (documented deadlock
  avoidances, correct ConPTY handle lifetimes, process-group kill, correct Windows quoting).
- **Command catalog as single source of truth**: palette, keybinding resolver, tooltips, and MCP
  `listCommands` all read the same `CommandDefinition` data — the keyboard-first rule is
  structural, not policed.
- **Remote auth**: one default-deny middleware per host (not per-endpoint checks), 128-bit CSPRNG
  tokens minted fresh per worker, constant-time compares, runner TLS options that fail closed
  (verified: `RunnerOptions.Resolve` refuses `--tls none` with a non-loopback bind), XSS-safe
  bootstrap injection (all JSON through the default `JavaScriptEncoder`).
- **Test shape**: the Playwright pyramid matches the integration-testing spec (full functional
  suite on `headless`, tagged delta on `remote`), runs cross-OS on every PR, and the claude stub
  (`claude.path` + `Weavie.FakeClaude`) is a real seam with zero test code in the production path.

## Findings

### F1 — The bridge has no cross-language contract; drift fails silently (HIGH)

The message shapes exist in three independent copies: hand-written TS unions in
`src/web/src/bridge.ts` (~85 variants, compile-checked on the web side only), free-hand C# JSON in
12+ files (three different mechanisms: interpolated strings, reflection `JsonSerializer.Serialize`,
`Utf8JsonWriter` builders), and a deliberately loose `e2e/mock-host.ts`. Mirroring is by comment
convention ("Shapes mirror the web's `WebBoundMessage` union").

Failure modes, verified:

- A host→web message whose `type` no listener recognizes vanishes with **zero trace** —
  `deliverFromHost` (`bridge.ts:645`) has no unhandled-type sink.
- A web→host unknown type hits `default:` and logs to console only (`HostCore.WebBridge.cs:378`).
- Field renames degrade to defaults via the lenient extractors (`GetStringOrEmpty`,
  `GetBoolOrFalse`) — a renamed `forever` field silently turns "dismiss forever" into a snooze.
- Drift has already happened: `case "connect-notion"` (`HostCore.WebBridge.cs:112`) is dead code —
  nothing in the web sends it.
- There is **no version handshake**. A remote worker at a different build than the page speaks a
  different dialect over the same bridge, and per the above, mismatches degrade invisibly. The
  existing compat idiom (fall back to the active session on unknown `slot`,
  `HostCore.WebBridge.cs:1256`) is flagged in its own comment as able to hide misrouting.

The only thing guarding parity today is the e2e suite, for the flows it exercises.

### F2 — Correlated requests have no reply guarantee; one path hangs forever (HIGH)

`RunCommandSafeAsync` catches only `UnknownCommandException or InvalidOperationException`
(`HostCore.WebBridge.cs:1080`). Any other exception escapes into a fire-and-forget task
(`_ = SomeAsync(...)` — the pattern of every async dispatch handler), no `command-result` is ever
posted, and the web's `invokeCommandOnBackend` promise (`bridge.ts:619`, no timeout — its doc
comment "Always resolves" is false on this path) settles only on socket close, **which the
in-process native transport never has**. Adjacent: `PostCommandResult` embeds `result.DataJson`
raw into a hand-built frame (`HostCore.WebBridge.cs:1097`) — one malformed fragment corrupts the
frame, the web logs "bad JSON", and the caller strands the same way.

More broadly there is no structural "every `id`/`token` gets exactly one reply" invariant: the
dispatch backstop contains handler throws as a log line, so what the user experiences depends on
which of the **six** independently implemented pending-map/timeout styles that feature happens to
use (fs: 10s loud reject; commands: fail-on-disconnect; branches/PRs: see F4).

### F3 — Cross-backend session switches can silently lose edits (HIGH)

`HostFileProvider` posts `fs-*` via `postToHost` — the **active** backend
(`host-file-provider.ts:60`, `bridge.ts:942`) — not the backend that owns the file, and
`fs-*-result` replies are dropped by the active-backend gate if the backend changed mid-flight.
`switchToSession` flips the active backend immediately (`App.tsx:321`); the outgoing session's
dirty working copies flush later (`rebindSession → releaseAll → flushSave`,
`editor-host.ts:467,260`), so those `fs-write`s land on the **new** backend, whose host rejects
them ("Path is outside every session worktree"). The edit is lost with a toast; the 1.5s
error-hold gate widens the dirty window well past the debounce. Same-backend switches are safe
(host routes by path); cross-backend ones break the core promise that a remote session is just the
same web layer pointed at another host.

Related: `WebSocketHostBridge` explicitly supports multiple connections and mirrors every reply to
all of them, but both tabs mint colliding correlation ids (`fs1`, `c1`… both start at 1) — tab A
resolves its pending `fs1` with tab B's `fs-read-result` (wrong bytes into a working copy).

### F4 — Silent-empty fallbacks, in a repo whose top rule is "no fallbacks" (HIGH as a class)

- `requestBranches` / `requestPullRequests` / `resolvePullRequest` resolve `[]`/`null` after 10s
  with no user-visible error (`bridge.ts:1016,1071,1097`) — the New Session typeahead renders
  "nothing found" over a dead backend. Host-side, `list-branches` maps `GitException` to an empty
  list with no error field (`HostCore.WebBridge.cs:1045`). Contrast `find-in-files`, which does
  this correctly.
- Hook-bridge death is console-only: if the pipe can't bind, `HookBridgeServer.ServeAsync` retries
  forever logging to `Console.WriteLine`; every relay call then eats a 5s connect timeout and fails
  open — change tracking, observed permission mode, and allow-all silently stop, and each edit
  gains ~10s of invisible latency. A `SocketException` similarly kills `McpServer`'s accept loop
  unobserved.
- ~40 dispatch paths open with `if (_session is not { } s) return;` — a message arriving before
  `StartAsync` completes is dropped without trace, and `BuildBootstrap()` called early silently
  emits `__WEAVIE_LSP__ = null` instead of throwing.
- Two timing-based sleeps in session provisioning (`Task.Delay(2500)` to seed the first prompt,
  `Task.Delay(1s)` before worktree removal, `HostCore.Sessions.cs`) — the prompt seed will misfire
  on slow starts.
- macOS PTY shim fallbacks: a production bundle missing the dylib silently loses controlling-TTY
  and resize (`PosixPtyTerminal.cs`) — justified for tests, unguarded in production.
- `SettingsStore`: an invalid individual value falls back to default with a log-only sink; the
  malformed-*file* case is toasted, the malformed-*value* case is not.

### F5 — Remote security gaps (HIGH first item, rest MEDIUM)

1. **The remote-security test suites never run in CI.** `.github/workflows/ci.yml` runs
   `dotnet test` only for `Weavie.Core.Tests` and `Weavie.Hosting.Tests`. `Weavie.Remote.Tests`
   (the black-box auth matrix), `Weavie.Runner.Tests` (the TLS fail-closed proofs), and
   `Weavie.Headless.Tests` are never executed on PR — an auth-middleware regression merges green.
2. **Worker token on argv**: `HeadlessLauncher` passes `--token <t>` on the command line —
   world-readable via `/proc/<pid>/cmdline` by any local user. `ListenMode.Resolve` already
   accepts `WEAVIE_SERVE_TOKEN`; the fix is one line in `Spawn`.
3. **Model→exec escalation under `claude.allowAllTools`**: `HookPolicy.Decide` auto-allows any
   non-edit, non-interactive PermissionRequest — including the registry server's
   `setSetting claude.path=/anything`, which the next pane restart executes. The hook-bridge doc
   treats `setSetting claude.path → RCE` as the token-theft threat; the same path is open to the
   model itself once the toggle is on.
4. **POSIX launch quoting is injectable**: `PosixPtyLauncher.FormatExecArgs` single-quotes values
   without escaping embedded `'`, skips quoting anything starting with `-`, and `exec '{claude}'`
   has the same hole — `claude.path` is user- and (per item 3) model-settable.
5. **Orphan workers**: no parent-death binding anywhere outside the Windows job object. A SIGKILL'd
   runner leaves the worker serving forever with a token nobody holds; in secured modes the
   restarted runner's worker then can't bind the pinned port and crash-loops into the breaker
   while the orphan keeps running. Same for the worker's own children on Linux — the remote
   deployment, where hard kills are most likely.
6. **`--remote` defaults to `0.0.0.0` over plain HTTP** (token-gated but cleartext, token in query
   strings). The architecture-overview claim "an exposed bind without TLS fails closed" is true of
   the runner only.
7. Minor: tokens in URLs (spec-deferred, now top of the hardening queue), no inbound WS message
   size cap (unbounded `MemoryStream` reassembly), duplicated hand-rolled constant-time compares
   in runner and headless where `CryptographicOperations.FixedTimeEquals` exists.

### F6 — `McpServer` is the seam that most violates the project's own standards (MEDIUM-HIGH)

The constructor takes six nullable injected stores plus a `registryMode` bool
(`McpServer.cs:47-104`); null-ness *is* the feature flag, IDE-vs-registry differences are encoded
in argument-null patterns, and mis-wiring is caught only at runtime via
`Require(...) → ToolUnavailableException` — a per-call fallback for a wiring error.
`IdeIntegration` mirrors this (seven nullables) and instantiates the class twice with different
null patterns. CLAUDE.md bans exactly this ("provide a Noop, require the real thing").

Compounding it, MCP tools are a **second registration system parallel to commands**: a new tool
touches a hand-written JSON schema string (`McpServer.ToolSchemas.cs`), a case in a 29-way
name switch, a `Handle*` method, and possibly the constructor stitching. Schema and handler have
no compile-time relation (the `openFile` schema advertises `startText`/`endText` the handler never
reads). Commands already solved this shape declaratively; the mcp-registry concept says
capabilities are "registered in Core and surfaced as MCP tools", but only commands and settings
are registry-shaped — layout/theme/editor tools are hard-coded.

Smaller instances of the same standards drift: `ProcessSupervisor` takes nullable `log`/`clock`;
`HookBridgeServer` takes a nullable `decide`; `HostServices.CreateDefault`'s `http: null` /
`path: null` convention; the hardcoded `Ctrl+N` pane badges in `App.tsx:117` that ignore
user-rebound `focusPaneByIndex` keybindings (the one violation of "read the effective binding
from the catalog" found).

### F7 — God files past their seams (MEDIUM)

Length is the signal, mixed responsibilities the violation — all of these mix them:

- `HostCore.WebBridge.cs` (1,280): the 60-case dispatch switch (fine) **plus** the full
  review/hunk state machine (~300 lines), scratch save-as flows, find-in-files, recent-files,
  and the web-command round-trip. HostCore totals ~3,164 lines across 8 partials split by
  accretion; by the Core-first rule it is the only place features can land, so it grows
  monotonically. The antidote already exists in-repo: the `ShellController` pattern (one switch
  line per message, logic in a collaborator).
- `App.tsx` (1,150): layout/fullscreen/pane state, eight dialog state machines, a ~20-case
  host-message switch, ~40 command registrations, session flows, file-index state. The switch and
  registrations are pure wiring with no JSX dependency.
- `bridge.ts` (1,126): ~540 lines of protocol types + two transports + backend registry +
  feature-specific request helpers (branches/PRs/source-token) that belong to their features.
- `inline-diff.ts` (1,454): diff rendering, PR comment threads, review state machine, and a
  hand-built DOM toolbar constructed three near-identical times (`buildAppliedBar`,
  `buildPrBar`, `renderParked`) — imperative DOM duplication in a Solid app.
- `SettingsStore.cs` (804): Tomlyn syntax surgery, three-source resolution with three
  near-duplicate coercion tables, watcher/debounce machinery, and MCP presentation JSON.
- `TerminalController` (487): dual-mode by a `"claude" | "shell"` string with six post-construction
  mutable properties whose assignment-before-first-launch ordering is honored only by convention;
  `HostSession`'s 130-line constructor starts servers and watchers, so a ctor throw leaks a listening IDE server.

### F8 — Remaining notables (LOW-MEDIUM)

- `ProcessSupervisor` stop-vs-restart TOCTOU: `RestartAfterDelayAsync` launches outside the gate,
  so a concurrent `Stop()`/`Dispose()` in that window leaves a live orphan child (no backstop on
  mac/Linux). Under `RestartPolicy.Always` the breaker also counts clean exits as crashes — six
  deliberate `exit`s in the shell pane within 60s prints "crashed repeatedly — stopped", and `Start()` from `Failed` doesn't clear the
  restart window.
- Settings→web fan-out is a manual three-place chain (register definitions, `Keys`+`BuildJson`
  class, `_onSettingChanged` branch, `BuildBootstrap` line); nothing keeps bootstrap and live push
  in sync.
- Per-instance `~/.weavie/weavie-<port>.{mcp.json,settings.json,system-prompt.txt}` files are
  never deleted on dispose (the mcp.json contains the Bearer token, stale after exit).
- `~/.zshrc` can re-export the `ANTHROPIC_API_KEY` the launcher stripped (claude launches through
  `zsh -l -i -c`) — billing-intent gap.
- `WEAVIE_FAKE_PRS`/`WEAVIE_FAKE_NOTION` env-var branches live in the shipping Headless
  composition root — harness fakes, but exactly the buried-env-var shape the repo bans; they
  belong behind the injected-services seam like `claude.path`.
- Doc drift: CLAUDE.md and the integration-testing spec name `TerminalController.ResolveClaudeLaunch`
  as the stub seam (it is the session-*resume* resolver; the real seams are
  `IPtyLauncher`/`ITerminal`); process-supervisor.md still lists LSP as "not yet wired" (it is);
  the architecture overview's TLS fail-closed sentence is runner-only.
- Test gaps: `WebSocketHostBridge` (the subtlest concurrency in the remote path) has zero unit
  tests — `Weavie.Headless.Tests` is a one-test scaffold that lists the missing coverage itself;
  the hunk-coordinate math in `inline-diff.ts` guards destructive disk splices behind a Monaco
  import with no sub-e2e tests (the extracted `diff-geometry` module is the proven remedy
  pattern).

## Roadmap

### P0 — small diffs, immediate payoff

1. **CI**: add `dotnet test` for `Weavie.Runner.Tests`, `Weavie.Remote.Tests`,
   `Weavie.Headless.Tests` (and the missing projects to the format loop). One step closes the
   biggest assurance hole (F5.1).
2. **Worker token via env**: `HeadlessLauncher.Spawn` sets `WEAVIE_SERVE_TOKEN` on
   `ProcessStartInfo.Environment` instead of `--token` argv (F5.2).
3. **POSIX quoting**: escape `'` as `'\''` in `FormatExecArgs` and the `exec` string (F5.4).
4. **Reply guarantee for commands**: widen `RunCommandSafeAsync` to `catch (Exception)` →
   `CommandResult.Failure`, and stop embedding `DataJson` raw (F2).
5. **HookPolicy carve-out**: execution-config-mutating capability tools (`claude.path`,
   `terminal.shell`, …) keep prompting under `allowAllTools` (F5.3).
6. **Pane badge**: derive from the `focusPaneByIndex` catalog binding via `formatKey` (F6).

### P1 — correctness and observability

7. **Bind fs traffic to the owning backend** (capture the backend id into the working-copy ref at
   `ensureRef` time, post via `postToBackend`) and route `fs-*-result` by correlation id like
   `command-result` instead of the active-backend gate; prefix web correlation ids with a
   per-page nonce (fixes F3 and the two-tab collision in one move).
8. **One request/response primitive**: web-side `request<T>(backendId, msg)` with a single
   pending map and an explicit per-call outcome policy (timeouts reject or notify, never resolve
   empty — retiring the branch/PR silent-empties); host-side, wrap the fire-and-forget dispatch
   tasks so every id-carrying message posts exactly one reply, error included (F2, F4).
9. **Protocol parity test + version handshake**: a ~50-line test that extracts `type` literals
   from the TS unions and diffs them against the C# `case`/builder strings (would already have
   caught `connect-notion`); include `buildNumber` in the `ready` ack and toast on remote
   mismatch (F1, cheap half).
10. **Health surfacing**: route `HookBridgeServer`/`McpServer` liveness into
    `SessionStatusMachine` → rail indicator; catch-all + resurrect in the pipe serve and MCP
    accept loops (F4).
11. **Worker lifetime**: worker exits on runner stdin EOF (or PDEATHSIG/job object in the
    supervisor start delegate); default `--remote` bind to loopback with `0.0.0.0` an explicit
    opt-in (F5.5, F5.6).

### P2 — structural refactors (do opportunistically, in this order)

12. **Split `McpServer`** into `IdeMcpServer` and `RegistryMcpServer` over a shared
    JSON-RPC/WebSocket transport — all deps non-nullable, `registryMode` and
    `Require`/`ToolUnavailableException` deleted; register tools as
    `McpTool { Name, SchemaJson, Handler }` records so a tool is one declaration, matching
    commands. Keep the hand-tuned schema descriptions — they are load-bearing prompt text (F6).
13. **Extract feature controllers from HostCore** on the `ShellController` pattern —
    `ReviewController`, `PullRequestController`, `ScratchSaver` — leaving `WebBridge.cs` as
    switch + push one-liners (~450 lines); gate started-state in one `StartedState` record set at
    the end of `StartAsync` so the ~40 null-guards collapse to one explicit gate and
    `BuildBootstrap` throws before start (F7, F4).
14. **Split the web god files along their existing seams**: bridge.ts →
    `protocol.ts`/`transport.ts`/`backends.ts` with feature helpers moved out; App.tsx →
    `app-messages.ts` + `app-commands.ts` + a dialogs module (~300-line component remains);
    inline-diff.ts → `diff-render` + a Solid `ReviewToolbar` component (collapses the tripled
    toolbar) + `pr-comments`, extracting the hunk math into a pure tested module (F7).
15. **One wire-type source of truth** (the full F1 fix): define the message shapes as C# records
    in `Weavie.Core` with a source-generated `JsonSerializerContext` (also resolving the
    reflection-vs-trim inconsistency and the three serialization styles), and generate the TS
    unions from them at build time. Migrate incrementally as messages are touched; keep
    `term-output` on its hot-path writer behind the same record type. Do this after item 9
    proves where the drift pain is.
16. **`SettingsStore` split** (`TomlSettingsFile` + resolution core + `SettingsCatalogJson`) and a
    declarative `WebSettingsProjection { Keys, BuildJson }` list iterated by both bootstrap and
    the change reaction (F7, F8).
17. **Supervisor hardening**: launch-epoch check to close the stop-vs-restart race; distinguish
    clean exits from crashes in the breaker message; clear the restart window on `Start()` from
    `Failed` (F8).
18. **Fill the test scaffolds**: `WebSocketHostBridge` unit tests (broadcast, slow-consumer drop,
    teardown races), inbound WS size cap with a loud 1009 close, shared
    `CryptographicOperations.FixedTimeEquals`, move the `WEAVIE_FAKE_*` branches behind the
    services seam, and fix the doc drift listed in F8.

## Method

Five parallel subsystem reviews (Core/Hosting layering; bridge protocol; process/PTY/hook-bridge;
web frontend; remote + tests), findings cross-checked against the code before inclusion. The
highest-severity claims (F2, F3, F5.1–F5.4, F6) were independently re-verified at the cited lines.