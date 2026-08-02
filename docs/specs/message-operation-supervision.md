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
   to cross a UI lane that may itself be synchronously closing. Explicit pre-shutdown drain remains a separate operation.
2. Admission selects an exact host or `(slot, incarnation)` endpoint and creates a supervised operation before
   handler code can run. Handler continuations never run inline from admission.
3. Every operation has one identity and reports its current stage: feature queue, handler dispatch, handler, or
   after-response work. Slow and failed logs include that identity, endpoint, peer, request id, feature, name,
   stage, and elapsed time.
4. At two seconds, an unfinished operation raises a keyed busy notification for its originating page. Slow reporting
   and the absolute deadline run independently, so blocked diagnostics cannot postpone timeout. Completion
   clears it. At the global `messaging.operationDeadlineSeconds` deadline (sixty seconds by default), the same key
   becomes a persistent error and a request receives the same detailed failure.
5. The deadline covers time waiting in a serialized feature lane, UI-dispatch admission, handler execution, and
   after-response work. A queued operation that expires never enters its handler. A running operation is fenced:
   its endpoint stops accepting work, its response is settled once, and late completion cannot answer or publish
   through the failed bus.
6. Cancellation callbacks and peer-disconnect callbacks run away from ingress. User code cannot capture the
   transport or ingress call stack through cancellation.
7. Managed code cannot safely abort an arbitrary running task. A timed-out operation therefore marks the worker
   unhealthy. The remote runner probes worker health independently, reports an unhealthy generation to
   `ProcessSupervisor`, kills its process tree, and lets the existing crash policy/backoff/breaker launch a clean
   generation on the same endpoint. Native hosts retain the detailed visible failure and fenced endpoint; session
   subprocess isolation is tracked separately.
8. Health is not process liveness. A health response includes ingress responsiveness plus the active/last failed
   message operation. A live process with an unresponsive ingress or a timed-out operation is unhealthy.

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
