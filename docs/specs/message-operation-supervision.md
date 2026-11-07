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
   "Running “<command title>”") and declares its [severity](#severity); neither has a default. At two seconds, an
   unfinished operation logs its identity, stage, and elapsed time and raises a keyed busy notification for its
   originating page that says what is slow and why: queued behind another activity in its feature lane ("Saving a
   file is waiting for another task to finish: Reading a file…"), waiting for Weavie's other work, or itself running
   long ("… is taking longer than usual…"). A stage change restates the notification. A handler registered with
   `HandleWithCallerProgress` declares that its caller already shows the wait in place (e.g. the branch field's
   "Suggesting…"), so its operation has no slow watch. Slow reporting and the absolute deadline run independently, so
   blocked diagnostics cannot postpone timeout. Completion clears the busy notification. Operation identities appear
   only in logs and health snapshots.
5. The global `messaging.operationDeadlineSeconds` deadline (sixty seconds by default) covers time waiting in a
   serialized feature lane, handler execution, and after-response work. A deadline ends one operation, never its
   endpoint: the request receives a failure, the handler's token is cancelled, teardown stops waiting for it, and the
   bus keeps accepting work. A queued operation that expires never enters its handler. UI-dispatch admission is
   instead covered by the ingress health probe because no application operation exists before an envelope is admitted.
6. Cancellation callbacks and peer-disconnect callbacks run away from ingress. User code cannot capture the
   transport or ingress call stack through cancellation.
7. Managed code cannot safely abort an arbitrary running task, so a lane stays held until its real handler returns
   (droppable operations excepted) and serial-per-feature execution survives a timeout. A late return still runs its
   after-response work, with a cancelled token, so command leases and ordered save completions always release. Only a
   successful late return clears the failure notice: the user is told an action "may still complete", which is the
   same unknown-outcome contract as a dropped transport.
8. Health is not process liveness, and a timed-out operation is not a wedged worker. `/control/health` answers 503 only
   when ingress is unresponsive: that is the one state the runner replaces a worker for, by reporting the generation
   to `ProcessSupervisor`, killing its process tree, and letting the crash policy/backoff/breaker launch a clean
   generation on the same endpoint. A responsive worker with a recorded non-droppable timeout is degraded: the monitor
   leaves it running, but update probation fails it so a bad build rolls back. A responsive worker with an active
   operation remains on probation, so a deterministic stuck replay cannot reset the breaker before its deadline;
   no-deadline operations are excluded, so an open dialog cannot pin probation. `/control/health` is mandatory in
   runner↔worker spawn contract 3. Launch arguments, worker status, hot updates, respawns, confirmation, and rollback
   all require an exact contract match; no worker runs with a reduced supervision surface.

## Severity

Severity answers one question: if this operation never finished, can the session's state still be trusted?

| Severity | Meaning | At the deadline | Lane |
|---|---|---|---|
| Droppable | Read-only and superseded by a later run | Failure to the caller, no notice | Released at the deadline |
| Action failed | The user can retry; the session is fine | Keyed error notice | Held until the handler returns |
| Session critical | State can no longer be trusted | The session requires a reload | Held until the handler returns |
| No deadline | Cancelling would be worse than waiting | Never cancelled; a persistent "still running" notice with Cancel | Held until the handler returns |

A critical operation that expires while queued also requires a reload, naming its blocker: it never ran, but the user's
work did not land. Failure notices coalesce per endpoint, feature, and name with a running count, so per-keystroke events
behind a stuck lane raise one notice. A notice for a queued expiry offers Reload, because a held lane has no other exit.

Session critical cannot be declared on the host bus: host state has no incarnation to replace, and host-level loss of
trust is already defined by unresponsive ingress. Commands declare severity on `CommandDefinition`, since one route
multiplexes them.

## Reload required

Reload-required is session state carried on the session catalog, which reaches every page, background sessions, and
reconnects, and does not depend on any session lane. The rail marks the session and the selected session shows a
persistent bar until the user reloads. The agent and terminals keep running meanwhile; the user chooses when to stop them.

`weavie.session.reload` runs on the host route, so it stays reachable with every session lane stuck. It replaces the
incarnation in one catalog push without flushing the editor, because the flush can block on the stuck save itself. The
conversation resumes; shells and any running turn stop, so every user entry point goes through
`weavie.session.reloadPrompt`, which confirms first. Each page owns its unsaved text: when the catalog replaces an
incarnation it captures dirty models into the slot-keyed session draft, leaves them clean so they can close, and writes the
text through the new incarnation, removing each entry once its own write lands. Files that weren't open stay closed. A
session awaiting reload neither blocks switching away from it nor raises save errors; the bar already says what's wrong.

## Pane state

Structured-agent pane state has one small in-memory owner. Mutations and snapshots are ordered there, but disk
reads/writes, JSON serialization, batching, and transport publication run in dedicated ordered workers. A slow
filesystem or page can delay its own operation without owning the pane state lock or the message ingress path.
This applies to every structured provider; terminal-backed providers use their existing supervised PTY path.

## Scope

The supervision boundary is the shared host/session message bus, so Claude Code, Codex, LSP, editor, terminal,
and lifecycle features receive the same admission, diagnostics, deadline, and containment behavior. Provider
processes keep their own `ProcessSupervisor` lifecycle.

## Known limits

- A write stuck inside an OS call cannot be cancelled and can land after Reload, over restored text.
- Reload can hang if a session file-activity consumer is wedged; only an app restart recovers that.
- Unsaved text is lost if its page closes for good while a reload is pending. A refresh is safe.
- An action reported as failed can still land, so a retry can repeat it.
