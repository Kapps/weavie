# Session-owned message bus

**Status:** implemented.

This is the bridge protocol and ownership contract for every native and WebSocket host. There is one
protocol, with no legacy flat-message lane and no active-session routing.

## Envelope

Every frame has the same shape:

```ts
interface MessageEnvelope {
  scope: "host" | "session";
  session: { slot: string; incarnation: string } | null;
  kind: "event" | "request" | "response" | "cancel";
  requestId: string | null;
  feature: string;
  name: string;
  payload: unknown;
  error: string | null;
}
```

The parser rejects contradictory or incomplete frames:

- host scope requires `session: null`;
- session scope requires non-empty slot and incarnation;
- events require `requestId: null`;
- requests, responses, and cancels require a non-empty request id;
- only responses may carry an error;
- feature, name, payload, session, request id, and error are always present.

Routing uses the envelope metadata only. Payloads contain domain data, never duplicate routing ids.

## Endpoint graph

`HostMessageRouter` owns:

- one host `MessageBus`;
- one `SessionMessageRouter`, keyed by the full `SessionAddress`;
- `ViewBindings`, keyed by transport peer, page epoch, and exact session.

`HostSession` owns its `SessionEndpoint`, feature handlers, controllers, and `SessionTaskScope`.
Construction may publish, but the endpoint's transport gate holds those frames. Before activation,
the host publishes the session's bounded initial snapshot into that gate. It first publishes the
exact address in the catalog, starts any structured agent runtime, then activates the endpoint,
registers it with the router, and flushes the snapshot and any construction-time frames in publication
order. A `ClientSession` created by that catalog consumes the gated snapshot; it does not issue a
competing sync that could overtake a later live event. The endpoint quiesces/removes itself before
disposing its resources.

`HostConnection` owns the client host bus and a map of exact addresses to `ClientSession`. A catalog
entry creates or closes those objects. `registerSessionFeature` installs a feature on every live
session, including sessions added later, so feature authors do not write catalog loops or selection
filters.

## Feature placement

Use the host bus only when one answer is valid for the whole connected host:

- connection hello and session catalog;
- user settings and command catalog;
- window, clipboard, and platform actions;
- host layout, rail, search preferences, recent files, updates, and remote-host registry.

Use a session bus for anything owned by a workspace session:

- agent pane/input/controls and terminal panes;
- editor state, files, source documents, review, git status, and pull-request status;
- LSP channels and configuration;
- test execution, notifications, attention, and session commands.

Command invocation is `session/commands/invoke`; ownership comes from the bus and never from a
`source` field in its payload.

## Request semantics

Request identity is `(peer, requestId)`, so two clients may use the same textual id safely. Responses
are unicast to the requesting peer. Cancellation is validated against feature and name before it can
abort a handler.

Inbound requests are admitted and tracked before handler code begins. Quiescing therefore cannot
miss a handler that synchronously starts shutdown. A disconnect cancels that peer's inbound work and
fails its outbound view requests.

There is no retry or mutation replay in the bus. If a request loses its transport before a response,
the caller receives a connection failure. Features that need idempotency define it in their domain
protocol. A failed reply delivery does not retry the handler or turn its successful result into a
second error reply. Deferred teardown runs after the reply attempt, so an invoking session can unload
or delete itself without either deadlocking its own dispatch or surviving solely because its peer
disconnected. Reconnect restoration uses a bounded state snapshot through `lifecycle.sync` plus
feature-owned pull protocols for unbounded data such as agent history. The sync snapshot is unicast
to the requesting peer; connecting one page never replays stale snapshots through already-live pages.

## Ordering

Each endpoint has one lane per feature:

- events and requests in the same feature execute in receive order;
- different features execute in parallel;
- different session endpoints execute in parallel;
- `HandleConcurrent` bypasses the feature lane for explicitly independent work.

After lane admission, every host-scoped handler enters through `IUiDispatcher`; this is the native
affinity and host-state serialization boundary. Session-scoped handlers execute directly and retain
their cross-feature and cross-session parallelism. Transport callback affinity is not a handler
guarantee.

The lane is a feature consistency boundary, not a global queue. A slow search cannot block terminal
input, and one session cannot block another. Messages that read or mutate the same state belong to
the same feature even when another surface renders the result; for example, `agent.openPlan` reads
agent transcript state and publishes its successful result through `editor.agentPlan`.

Remote outbound transport preserves FIFO order within each exact `(scope, session, feature)` route
and round-robins lazily encoded oversized-message chunks with small messages from other routes. One
connection carries at most one partial oversized body while that interleaving is active, and its
outbox is bounded by logical count and a retained-character budget. One body may exceed that budget,
but its saturated weight makes it the outbox's only retained message. A large response therefore
cannot prevent an unrelated feature or session from receiving its next message or multiply receiver
memory across many partial large bodies.

## Presentation

Client selection is local UI state. It publishes `view.attach` on the chosen session. Selecting a
session on another host first detaches the previous host's view; attaching a peer elsewhere also
displaces its old binding server-side. `registerViewFeature` installs transient presentation
handlers only for that binding; ordinary owned handlers use `registerSessionFeature` and install on
every live session.

Each attachment carries the page epoch. Native windows reuse one physical peer across reloads, so a
new epoch replaces the previous page generation even when the peer and selected session are
unchanged. Replacement settles that generation's outstanding host-to-view requests before new ones
can be admitted. A duplicate attach from the same epoch is a no-op, and an old epoch cannot detach
its replacement.

The local host persists the last client-selected `(backendId, slot)` as rail UI state. After its
hello, the client resolves that stable location against the live catalogs and reattaches the view.
The persisted selection never enters a session envelope and is never consulted for domain routing.
An unloaded slot remains a valid preference; a catalog that authoritatively removes the slot selects
and persists a fallback.

Only host-to-web actions that require a presently mounted surface consult `ViewBindings`:

- focus a pane or omnibar (transient events);
- execute a web-only command in the selected view (a correlated request).

The router rechecks the binding after request admission. Detach or displacement sends cancellation
for the exact outstanding request.

The few web-to-host mutations derived from the shared widget—editor session, active-editor, and
open-editor snapshots—admit only the peer bound to that session. Admission is captured before the
feature lane: a valid queued mutation still runs if the user switches afterward, while a stale or
unbound page cannot author the host snapshot. This is presentation authorship, not a current-session
filter; ordinary session messages never consult the binding.

All durable session events broadcast with their exact address and never consult the binding. The web
stores them by `ClientSession`; render code may compare selection after state mutation to decide
whether to repaint.

`SessionState` retains the latest ordered payload for durable feature keys that do not justify a
dedicated store, including source documents and agent plans. Live mutation updates the retained value
and broadcasts the same payload atomically. `lifecycle.sync` replays those values only to its requester.

## Editor ownership

The editor widget is shared, but editor state is not:

- `OwnedEditorSession` is keyed by `ClientSession`;
- file models use session-namespaced URIs;
- file provider requests use the model owner's session bus;
- reviews and diff proposals carry their owning `ClientSession`;
- an off-screen open updates the owner's tab state and resolves its model when presented.

No projection lease, projection revision, active-session host state, or release handshake exists.

## Connection and lifecycle

`connection.hello` returns the host incarnation, complete catalog, layout, settings-derived
catalogs, and host state. The client does not route session frames until hello creates and validates
their exact owners. Frames that arrive early during connect or reconnect are buffered by address;
an unrelated catalog update cannot discard them, while a catalog entry for the same slot with a
different incarnation proves them stale. Sessions already live when hello completes receive
`lifecycle.sync`. For later loads, the host's endpoint activation gate guarantees that the catalog
frame precedes the new session's bounded initial snapshot and every later live frame.

Once hello is authoritative, a frame for an unknown address waits only behind catalog work already
admitted on the host bus. If that work does not create the exact owner, the frame is discarded; it
cannot remain buffered for a hypothetical future catalog.

Session teardown:

1. remove the endpoint from inbound routing;
2. cancel and drain handler dispatch plus `SessionTaskScope`;
3. allow final outbound owned events during resource disposal;
4. close the bus and reject later work.

The web closes a removed `ClientSession`, aborting handlers and pending requests. A later load of the
same slot has a different incarnation and a new object.

## Required tests

- a dummy feature routes to its owner while another session is selected;
- an old incarnation cannot enter a reused slot;
- same-feature handlers serialize while other features and sessions run in parallel;
- request cancellation and duplicate correlation are exact;
- peer loss during reply neither retries a mutation nor prevents endpoint quiescence;
- detach/displacement cancels only the affected view request;
- native reload replaces the page generation and settles its pending view request;
- publications made during construction wait for catalog activation and preserve order;
- a new session's gated initial snapshot precedes its live events without a competing client sync;
- a fresh structured-agent session starts on endpoint activation and accepts its first turn without a view switch;
- quiesce tracks already-admitted handlers and permits final owned events;
- reconnect validates addresses before releasing early traffic;
- reconnect snapshots are unicast to only the requesting peer;
- an unrelated catalog update cannot discard a future session's already-arrived first event;
- an unknown frame after an authoritative catalog is not retained for a future session;
- reload restores the stable client-selected slot without making selection a routing input;
- shared-widget snapshots admit only their bound author and remain valid after admission;
- background editor messages mutate only their owner;
- delayed file replies cannot repaint the selected session;
- unload/delete flush dirty models from the exact attached view and abort on save failure;
- two peers may reuse an LSP channel id without sharing a process or cleanup epoch;
- an unfocused/background completion still raises client-side attention.
