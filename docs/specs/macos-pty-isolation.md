# macOS child process isolation

Every owned macOS child starts in a separate OS session before its PID is exposed to
the supervisor. Redirected agents, tools, language servers and workers use
`OwnedProcess`; terminals use `PosixPtyTerminal`. Both share the native
`posix_spawn` adapter, which resets signal dispositions and the signal mask and
inherits only explicitly mapped descriptors.

## Process trees and GUI lifetime

Native ACP restart, session deletion and `/clear` stop the agent's process tree.
Tree termination is necessary because agents and shells launch descendants.
[.NET's Unix implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.Unix.cs)
stops a process before enumerating its descendants. On macOS, an orphaned process
group containing a stopped member receives SIGHUP and SIGCONT through
[XNU's orphanpg](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/kern/kern_proc.c).
A GUI host must therefore never share its process group with a tree it stops.
The shared launch boundary enforces this for every tree-killed child.

Redirected children spawn directly with their requested executable and pipes.
Launch errors return synchronously. A native waiter observes exit without reaping;
reaping and signaling share a managed gate so the owned PID cannot be reused
during termination. Process handles remain valid until pending exit waits finish,
even when a caller disposes the streams immediately after stopping the child.

## Terminals

The PTY adapter starts the bundled `weavie-pty-launcher`, which acquires the
controlling terminal and replaces itself with the command. A close-on-exec status
pipe reports setup and exec errors synchronously. The supervisor owns the same PID
throughout.

A raw fork inside a managed/AppKit host carries runtime handlers and descriptors
into the child. Before exec, a child signal can reach a runtime signal pipe shared
with the host. The executable boundary avoids host-runtime code running in the
child; see the [.NET launch implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/native/libs/System.Native/pal_process.c).

## Exit evidence

Console capture writes each completed line immediately to a private per-run file.
Process launches and stops record host and child PIDs; session teardown records
each resource boundary. The in-app log viewer exposes the saved path and any
persistence failure. The exit journal links the run's log, preserves signal reasons
over generic process exit, and archives unexpected prior-run evidence before
marking the new run live.

## Build and validation

`src/MacPty.targets` builds the native library and PTY launcher for the shared
macOS minimum version. Each executable consumer imports the target and owns the
assets in its output and publish directories; GUI hosts bundle them.

macOS CI tests session and controlling-terminal ownership, descriptor exclusion,
signal reset, exact launch errors and immediate PTY teardown. Managed tests cover
streams, exit results and kill/dispose/wait ordering. A LaunchServices GUI probe
asserts that an isolated child and its descendant can be tree-killed while the host
survives; an unisolated control reports whether the same environment reproduces
host termination. Full-stack tests exercise native `/clear`, restart and deletion.
Linux execution validates shared behavior; Darwin-specific claims require macOS
execution. The control result determines whether the reported disappearance is
reproduced, independently of the isolation invariants.
