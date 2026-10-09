# Message operation supervision

An inbound web message is untrusted work. Receiving bytes, admitting work, running application code, and
recovering a failed worker are separate responsibilities; no transport callback may execute a feature
handler or wait for one.

## Invariants

1. A transport callback only copies the peer/body into the host ingress queue and returns. The queue preserves
   arrival order for admission, view binding, cancellation, and disconnect signals. Its pump marshals only the
   bounded admission step through the host sequencing context; it never waits for a handler. Health probes cross
   that same boundary, so a blocked host/UI lane is observable rather than hidden behind a responsive queue.
   Shutdown cancels dispatcher admission and rejects queued probes; it never waits for unadmitted transport input
   to cross a UI lane that may itself be synchronously closing. Explicit pre-shutdown drain remains a separate
   operation. Admission diagnostics run away from the pump and cannot delay the next item.
2. Admission selects an exact host or `(slot, incarnation)` endpoint and creates a supervised operation before
   handler code can run. Handler continuations never run inline from admission.
3. Every operation has one identity and reports its current stage: feature queue, handler dispatch, handler, or
   after-response work. Slow and failed logs include that identity, endpoint, peer, request id, feature, name,
   stage, and elapsed time. Diagnostics use bounded, ordered workers; a blocked sink consumes one worker and a
   later coalescing summary makes any suppressed volume explicit.
4. Every handler registration names its activity in user terms ("Saving a file"; a command invocation uses
   "Running “<command title>”"). At two seconds, an unfinished operation logs its identity, stage, and elapsed
   time and raises a keyed busy notification for its originating page that says what is slow and why: queued
   behind another activity in its feature lane ("Saving a file is waiting for another task to finish: Reading a file…"),
   waiting for Weavie's other work, or itself running long ("… is taking longer than usual…"). A stage change
   restates the notification. A handler registered with `HandleWithCallerProgress` declares that its caller
   already shows the wait in place (e.g. the branch field's "Suggesting…"), so its operation has no slow watch.
   Slow reporting and the absolute deadline run independently, so blocked diagnostics cannot postpone timeout.
   Completion clears the busy notification. At the global `messaging.operationDeadlineSeconds` deadline (sixty
   seconds by default), every operation raises a persistent error under the same key ("Saving a file didn't
   finish within 60 seconds, so this session stopped responding.") and a request receives the same failure;
   operation identities appear only in logs and health snapshots.
5. The deadline covers time waiting in a serialized feature lane, handler execution, and after-response work. A
   queued operation that expires never enters its handler. UI-dispatch admission is instead covered by the ingress
   health probe because no application operation exists before an envelope is admitted. A running operation is
   fenced: its endpoint stops accepting work, its response is settled once, and late completion cannot answer or
   publish through the failed bus. After-response work receives endpoint shutdown cancellation, so closing never
   waits for work queued behind the same UI lane.
6. Cancellation callbacks and peer-disconnect callbacks run away from ingress. User code cannot capture the
   transport or ingress call stack through cancellation.
7. Managed code cannot safely abort an arbitrary running task. A timed-out operation therefore marks the worker
   unhealthy. The remote runner probes worker health independently, reports an unhealthy generation to
   `ProcessSupervisor`, kills its process tree, and lets the existing crash policy/backoff/breaker launch a clean
   generation on the same endpoint. Recovery state, termination, and restart scheduling do not depend on logging
   or state observers. Observer notifications are sequenced with their transitions and suppressed after disposal.
   Native hosts retain the detailed visible failure and fenced endpoint; session subprocess isolation is tracked
   separately.
8. Health is not process liveness. A health response includes ingress responsiveness plus the active/last failed
   message operation. A live process with an unresponsive ingress or a timed-out operation is unhealthy, including
   while an update is waiting for that worker to drain. A responsive worker with an active operation remains on
   probation, so a deterministic stuck replay cannot reset the breaker before its deadline. `/control/health` is
   mandatory in runner↔worker spawn contract 2. Launch arguments, worker status, hot updates, respawns,
   confirmation, and rollback all require an exact contract match; no worker runs with a reduced supervision surface.

## Pane state

Structured-agent pane state has one small in-memory owner. Mutations and snapshots are ordered there, but disk
reads/writes, JSON serialization, batching, and transport publication run in dedicated ordered workers. A slow
filesystem or page can delay its own operation without owning the pane state lock or the message ingress path.
This applies to every structured provider; terminal-backed providers use their existing supervised PTY path.

## Scope

The supervision boundary is the shared host/session message bus, so Claude Code, Codex, LSP, editor, terminal,
and lifecycle features receive the same admission, diagnostics, deadline, and containment behavior. Provider
processes keep their own `ProcessSupervisor` lifecycle. Moving each complete session behind a process boundary is
the follow-up needed to replace only one failed session rather than a whole worker.
