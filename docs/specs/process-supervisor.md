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
- `stop()` — kill/dispose the current instance; must be a safe no-op when nothing runs.

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
    Running --> Idle: clean exit (Never/OnFailure) · Stop()
    Running --> BackingOff: crash · any exit under Always
    BackingOff --> Running: backoff elapsed
    BackingOff --> Idle: Stop()
    Running --> Failed: > MaxRestartsInWindow
    Failed --> Running: Start()
    Failed --> Idle: Stop()
```

`StateChanged` carries `(State, ExitCode?, RestartCount)`. `ExitCode` is set when a process exit drove the
transition and `null` for a launch or an intentional `Stop`/`Dispose` — which lets a UI tell a real exit
(show a "process exited" notice) apart from a deliberate teardown (stay quiet, the pane is being reset).

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

`ProcessSupervisor` owns *graceful* teardown — its `stop` delegate kills the tree on a clean shutdown. But the
supervisor is managed code in cross-platform Core, and the orphan problem is the host dying with **no managed
code running** (a debugger Stop, a crash): no `Dispose`, no `stop`, nothing. The only thing that reaps children
then is an OS primitive. On Windows that's a **Job Object** with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`
(`Weavie.Win.Hosting.ChildProcessJob`, installed once at `Program.Main`): the host process is assigned to the
job, children inherit it, and when the host dies by any means the OS closes the job handle and terminates
everything still inside. This covers *every* child (Vite, ConPTY shells, language servers, `claude`), not just
supervised ones — so it's the backstop behind the supervisor, not a replacement for it. Mac/Linux hosts need
their own equivalent (`PR_SET_PDEATHSIG` / a process group) — not yet wired.

## Child-owned temp (forced stops leave nothing behind)

A child killed outright cannot clean up after itself, and a .NET child always leaves something: its runtime
creates `clr-debug-pipe-<pid>-*` and `dotnet-diagnostic-<pid>-*-socket` (plus `CoreFxPipe_*` for named pipes it
serves) in its system temp. Forced stops are inevitable, so the fix is ownership, not avoiding SIGKILL: every
non-interactive child gets a temp directory Weavie owns.

- **One choke point.** Every non-PTY child — ACP agents (persistent and transient), ACP terminal commands,
  language servers, the dev server, the runner's worker, and `ProcessCapture` one-shots — starts through
  `OwnedProcess.Start`, which points `TMPDIR` (Unix) or `TMP` + `TEMP` (Windows) at a `ChildTempScope` directory.
  A scope is deleted recursively once its owner has let go and the last child started in it is reaped — normal
  exit, crash, or SIGKILL — and a child's release happens *before* its exit is observable, so whoever waits for
  the exit also sees its temp gone.
- **Scopes follow what children share.** `OwnedProcess.Start(info)` gives the child a scope of its own. An ACP
  conversation's terminal commands share one scope for the conversation's life, because an agent chains commands
  through temp files (`f=$(mktemp)` in one, `cat "$f"` in the next).
- **One root per Weavie process.** Scopes live under an owner-only (0700) root `<user temp>/wvXXXXXXXX`
  (`ChildTemp.Root`) holding an `owner` record: the pid and its start stamp — Linux's `/proc/<pid>/stat` start
  ticks, elsewhere Unix seconds, both exact and stable. The name is random and creation verifies the mode and
  creates the record exclusively, so a directory another user prepared fails loudly. The root sits in the
  *user's* temp even for an owned Weavie (the runner's worker), so roots never nest. Scope directories are named by
  a base-36 counter that is never reused — short enough to keep a child's socket paths inside macOS's 104-byte cap,
  and a straggler a child left running can't share a successor's temp. A temp path the caller set explicitly in
  the child's environment is left alone.
- **Reclaim by dead owner, never by age.** On startup `HostCore` and the runner call
  `ChildTemp.ReclaimOrphans`, which deletes each sibling root whose owner is gone — no process has that pid, or
  the one that does started at another time (pid reuse). A directory without a readable `owner` record is not a
  root, and a live owner's root, or one this user cannot inspect, is never touched. A process that exits cleanly
  removes its own root unless a scope outlived it; a killed worker's root goes when the runner's next worker starts.
- **Terminals keep the user's temp.** PTY children (the Claude pane, shell panes) are the user's own sessions,
  so they keep the user's `TMPDIR`. An owned child carries the user's temp as `WEAVIE_USER_TEMP`, and
  `TerminalStartInfo.BuildEnvironment` restores it — so the runner's worker, itself an owned child, still gives
  its terminals the user's temp. The login-shell import ignores the probe's assigned temp for the same reason.
- **Rendezvous points stay with their owner.** Off Windows the hook bridge's socket (`HookProtocol.PipeName`) is
  an absolute path in the *host's* temp, handed to Claude by `WEAVIE_HOOK_PIPE`, and the open-with instance pipe
  (`InstanceProtocol.PipeName`) is an absolute path in the *user's* temp — so a relay, or a `weavie <path>` run
  from an agent's command, reaches them whatever temp it was given.

```mermaid
flowchart TD
  T["system temp"] --> R1["wvA1b2C3 — owner: 4100 (live desktop host)"]
  T --> R2["wvQ9x8Z7 — owner: 3900 (gone: reclaimed at startup)"]
  R1 --> C1["0 — csharp-ls"]
  R1 --> C2["1 — ACP agent"]
  R1 --> C3["2 — that conversation's terminal commands"]
  T --> RR["wvK4m5N6 — owner: 5000 (runner)"] --> W["0 — worker"]
  T --> WR["wvP7q8R9 — owner: 5012 (the worker)"] --> WC["0 — worker's ACP agent"]
```

## Follow-ups

- File logging to `~/.weavie/logs/<name>.log`, with a verbosity **setting**.
- A "processes" status panel in the UI, driven by `StateChanged` (state + restart count per child).
- Extend the kill-on-close orphan guard to the Mac/Linux hosts (`PR_SET_PDEATHSIG` / a process group); only
  the Windows host has it today (see *Orphan prevention* above).
