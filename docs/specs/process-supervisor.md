# Process supervisor

**Status:** core engine implemented + adopted by the terminal panes and the Vite dev server; further
adopters planned. Orphan-on-hard-kill is closed by a host-level kill-on-close Job Object (see below).

Weavie spawns several long-lived child processes (the embedded `claude` TUI, shell panes, language
servers, the dev server). Before this, each was managed ad-hoc by its own owner — none auto-restarted on
crash, there was no shared backoff or crash-loop protection, logging was scattered `Console.WriteLine`s, and
orphan cleanup was uneven. The supervisor is the one place that owns *process lifecycle*: launch, watch,
relaunch-with-policy, log, and clean teardown.

The hook relay (see [permission-modes-and-change-tracking.md](permission-modes-and-change-tracking.md)) is
**not** a supervised process — it is a transient, fork-per-tool-call helper that exits in milliseconds and
fails safe. Only *long-lived* children belong here.

## What "should it restart?" actually means

The motivating question: if your embedded `claude` crashes, you are left staring at a dead, empty panel —
what's the point? In an editor where the claude/shell pane is a permanent fixture, a stopped pane is never
the desired end state. So:

- **Crash** (non-zero / abnormal exit) → relaunch, with backoff.
- **Clean exit** (code 0) → under `Always` (the pane policy) it still relaunches; the clean-vs-crash
  distinction survives only as *timing* (a healthy run relaunches promptly; repeated crashes back off).
- **Crash-loop** (more than _N_ restarts inside a window) → **stop trying** and show a "stopped" notice.
  This is the *only* state that leaves a stopped pane, and only after we've tried hard — far better than
  hot-looping a broken binary and pinning a core.

## Engine

`Weavie.Core.Processes.ProcessSupervisor` owns the *policy and timing*, not the process handle. The caller
supplies two delegates, which keeps the supervisor agnostic to how a thing is spawned (a ConPTY terminal, a
`System.Diagnostics.Process`, anything):

- `start(SupervisedLaunch launch)` — launch a fresh instance and wire its exit back through
  `launch.NotifyExited`. `launch.Attempt` (0 = first, ≥1 = restart) lets the caller announce a restart in
  the UI. An exception thrown here is treated as a failed launch (a crash) and feeds the backoff/breaker.
- `stop()` — stop the current instance and return a `Task<ProcessStopOutcome>` that completes once it has
  exited; must be a safe no-op when nothing runs. See [Stopping](#stopping-graceful-then-forced).

`launch.NotifyExited(int exitCode)` is called from the child's exit callback (any thread). The handle ties
the exit to the specific instance, so a stopped predecessor's late exit is ignored rather than misattributed
to its replacement (which would restart a healthy child); an exit arriving while not `Running` is also ignored.

### Tunables (`SupervisionOptions`)

| Option | Default | Meaning |
| --- | --- | --- |
| `Policy` | (required) | `Never` / `OnFailure` (restart on crash only) / `Always` (restart on any exit) |
| `InitialBackoff` | 500 ms | delay before the first restart |
| `BackoffMultiplier` | 2.0 | growth per consecutive crash |
| `MaxBackoff` | 30 s | ceiling on the delay |
| `HealthyAfter` | 10 s | a run lasting this long resets the consecutive-crash count |
| `CrashLoopWindow` | 60 s | sliding window for the breaker |
| `MaxRestartsInWindow` | 5 | restarts allowed in the window before tripping `Failed` |

Backoff is `min(MaxBackoff, InitialBackoff × Multiplier^(consecutiveCrashes−1))`. A run lasting at least
`HealthyAfter` resets `consecutiveCrashes`, so an occasional crash after a long healthy run restarts promptly
rather than at a grown delay. Timing flows through an injectable `ISupervisorClock` so tests drive backoff
and the window deterministically (no real waiting).

### State machine

```mermaid
stateDiagram-v2
    [*] --> Idle
    Idle --> Running: Start()
    Running --> Idle: clean exit (Never/OnFailure) · StopAsync()
    Running --> BackingOff: crash · any exit under Always · ReportUnhealthy()
    BackingOff --> Running: backoff elapsed
    BackingOff --> Idle: StopAsync()
    Running --> Failed: > MaxRestartsInWindow
    Failed --> Running: Start()
    Failed --> Idle: StopAsync()
```

`StateChanged` carries `(State, ExitCode?, RestartCount)`. `ExitCode` is set when a process exit drove the
transition and `null` for a launch or an intentional `StopAsync`/`DisposeAsync` — which lets a UI tell a real exit
(show a "process exited" notice) apart from a deliberate teardown (stay quiet, the pane is being reset).

### Stopping: graceful, then forced

A stop asks the child to exit through **its own protocol** and force-kills it only past a deadline. SIGKILL skips a
.NET child's runtime cleanup and strands its IPC files (`dotnet-diagnostic-*`, `clr-debug-pipe-*`) and named pipes
(`CoreFxPipe_*`) in the temp directory, and since .NET 10 removed the runtime's default SIGTERM handler, a signal
doesn't help either — so the ask is protocol-specific:

| Child | Ask (`ProcessStopPlan`) | Deadline |
| --- | --- | --- |
| ACP agent (`AcpJsonRpcConnection`, `AcpTransientConnection`) | close stdin (behind the write gate, so an in-flight write finishes first) | 5s |
| Language server (`LspServerProcess`) | LSP `shutdown` request → `exit` notification → close stdin | 5s |
| Runner worker (`HeadlessLauncher`) | `POST /control/shutdown` → the worker's orderly `StopApplication` | 10s (an idle worker measured ~130ms; its own children get 5s each) |
| PTY terminals, Vite, agent-run commands (`AcpTerminal`) | their existing teardown (`ProcessStop.Immediately`) | — |

`ProcessStop.RunAsync(plan, exited, force, clock)` runs the escalation: an `IOException` from the ask means the
child is already gone (keep waiting for its exit); any other ask failure forces at once; past the deadline `force`
runs exactly once and the exit is awaited without bound (a killed child exits). The outcome is
`ProcessStopOutcome.Graceful` or `Forced(because)`.

```mermaid
sequenceDiagram
    participant S as ProcessSupervisor
    participant O as Owner stop delegate
    participant C as Child
    S->>O: stop()
    O->>C: ask (stdin EOF · LSP shutdown/exit · HTTP shutdown)
    alt exits before the deadline
        C-->>O: exited
        O-->>S: Graceful
    else deadline passes, or the ask fails
        O->>C: kill tree
        C-->>O: exited
        O-->>S: Forced(because)
        S->>S: log Warning, raise Stopped
    end
```

The supervisor runs stops **one at a time** and launches only after the stop before it has completed, so a
replacement never overlaps its predecessor — the ACP agents' single active writer, the runner's pinned port, and
LSP channel ids depend on that. `Start()` during an in-flight `StopAsync()`, and the restart scheduled by
`ReportUnhealthy`, both wait for the stop. A retired ACP generation's output is still read (and discarded) while it
shuts down, so a child writing on its way out never blocks on a full pipe.

A forced stop logs a Warning and raises `Stopped` — including the stop `DisposeAsync` runs. `HostSession` turns it
into a toast (`<name> didn't exit within 5s of <ask>; Weavie force-stopped it`) and an `ExitJournal` note, since at
app exit nobody sees the toast. Session teardown stops shells, the agent, and language servers in parallel, sessions
stop in parallel, and the Mac host starts every window's teardown before waiting on any, so deadlines overlap
instead of adding up.

### Logging

Structured `SupervisorLogEntry(Name, Level, Message)` go to an injected sink. Today the controllers route
them to the console (the existing convention). A per-process log file under `~/.weavie` is a planned sink;
per the no-buried-flags rule, any verbosity toggle lands as a **setting**, off by default — never an env var.

## Adopters

**Implemented — terminal panes** (`TerminalController`, shared by every host): permanent agent panes use policy
`Always`; closeable shell tabs use `Never` and retain their exit state until explicitly reopened. Agent crashes
relaunch with backoff; the supervisor reuses the existing
`term-reset` → `term-ready` → `Start` web handshake for intentional restarts (a `terminal.shell` setting change),
and on a supervised restart writes a one-line `[weavie] … restarting…` notice into the pane via the normal
`term-output` channel (no web-protocol change). A crash-loop trip prints a "stopped" notice and posts
`term-exit`.

**Implemented — Vite dev server** (`WebDevServer`, Debug only): policy `Always`. Each instance binds its own
free port (picked once, held for its lifetime) and spawns Vite with `--strictPort`; a mid-session crash is
relaunched on the same port, so the WebView's origin stays valid and its HMR client just reconnects. Bring-up
failure stops the supervisor so the error page's Retry gets a clean start. This per-instance port (vs. a fixed
5173 that a second instance silently *reused*) is what lets multiple worktrees / Debug instances run side by
side without cross-talk.

**Planned:**

- **Language servers** (`LspConnection` / `LspBridgeServer`): policy `OnFailure` (or `Always`). Caveat: a
  restarted LSP needs the full `initialize` handshake replayed and open documents re-synced — the `start`
  delegate must own that re-init, so this is more than dropping the supervisor in. Not yet wired.
- **Hook-bridge pipe server lifecycle:** the loopback pipe server is in-process (lives with the host), so it
  isn't supervised; only note that the *relay* it talks to is transient and exempt.

## Orphan prevention (hard kill)

`ProcessSupervisor` owns *graceful* teardown — its `stop` delegate asks, then kills the tree past the deadline. But the
supervisor is managed code in cross-platform Core, and the orphan problem is the host dying with **no managed
code running** (a debugger Stop, a crash): no `Dispose`, no `stop`, nothing. The only thing that reaps children
then is an OS primitive. On Windows that's a **Job Object** with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`
(`Weavie.Win.Hosting.ChildProcessJob`, installed once at `Program.Main`): the host process is assigned to the
job, children inherit it, and when the host dies by any means the OS closes the job handle and terminates
everything still inside. This covers *every* child (Vite, ConPTY shells, language servers, `claude`), not just
supervised ones — so it's the backstop behind the supervisor, not a replacement for it. Mac/Linux hosts need
their own equivalent (`PR_SET_PDEATHSIG` / a process group) — not yet wired.

Two stop paths still strand a .NET child's runtime files, so the tests mask exactly those sources with
`DOTNET_EnableDiagnostics=0`: an agent-run command (`AcpTerminal`) is killed as a tree because SIGTERM to its leader
would orphan the rest of the tree (Linux children share Weavie's process group), and an agent wedged on a write it
never drains can only be force-killed. PTY terminals still end with SIGTERM → SIGKILL, which a .NET 10 child
without its own handler doesn't survive cleanly. A graceful parent's grandchildren that ignore its exit are not
reaped on Linux without a process group.

## Follow-ups

- File logging to `~/.weavie/logs/<name>.log`, with a verbosity **setting**.
- A "processes" status panel in the UI, driven by `StateChanged` (state + restart count per child).
- Extend the kill-on-close orphan guard to the Mac/Linux hosts (`PR_SET_PDEATHSIG` / a process group); only
  the Windows host has it today (see *Orphan prevention* above).
