# Native agent pane state

The native structured-agent pane renders provider-neutral `AgentPaneMessage` records owned by
`AgentSessionHost`, without making session sync or the shared message transport proportional to the
transcript's size.

## Ownership

**The provider owns the transcript; Weavie caches nothing.** `AgentSessionHost` materializes the pane in memory
for as long as the session is loaded, and a cold load starts empty until the provider replays its own
conversation. A provider that cannot replay comes back empty rather than being handed a stale local copy that
looks live — the same reason `AcpAgentSession` emits `transcript-reset` when a persisted session can be neither
loaded nor resumed.

Switching between loaded sessions never touches this path: it is served from the in-memory pane at a settled
generation.

## Generations

A record is addressed by `(generation, ordinal, revision)`. Clearing the pane restarts ordinals, so the
generation is what lets a client tell new content from old — and a generation change is a specific claim:
*every ordinal you hold is void, re-fetch.*

It therefore changes only when content is genuinely discarded:

- a provider `transcript-reset` — the conversation is gone;
- a provider snapshot replacing a **non-empty** pane.

A snapshot filling an **empty** pane invalidates nothing, so it streams into the current generation as ordinary
live records. Restoring a transcript is not an epoch change, and treating it as one used to force every
connected client into a mid-load re-sync.

Clients must survive a generation change they did not see announced — `paneReset` is a broadcast, and a
reconnecting page can miss one. Observing a live record from a newer generation is itself sufficient notice:
the client discards its state and re-fetches history.

## Streamed history

`lifecycle.sync` publishes interactive state with agent controls first. Transcript transfer uses the shared
authenticated HTTP origin available on every platform, independently of WebSocket/native live messages.

The client requests `/weavie-agent-history` with its exact slot and incarnation. The host captures the
materialized records and generation/revision watermark under the pane lock, then serializes outside it.
The response is newline-delimited JSON: newest-first batches of up to 64 typed records, followed by an
explicit completion marker. Every batch carries the same generation, revision, and total record count.
There are no cursor requests, retained readers, or JSON-inside-JSON record fragments.

Serialization, writes, and flushes are awaited. HTTP/network buffering supplies producer backpressure;
the browser reads and applies batches sequentially rather than queueing detached work. This does not
require browser-render acknowledgements. Request cancellation and session shutdown cancel the stream.
Cross-origin reads require the explicit worker token; cookie-only requests remain same-origin.

The accumulator preserves generation/revision and cumulative-delta merging with live output. It publishes
the newest batch promptly, accumulates older records without repeatedly projecting the growing transcript,
and publishes the complete view at the final marker. A reconnect watermark advances only when that marker
matches the received count. Completed clients request only records changed since their watermark;
a generation change returns the complete replacement. Removing a client session or resetting history
aborts its outstanding HTTP read.

A stream failure leaves already-rendered text intact and shows a persistent error with the Reload Agent
History command and its effective shortcut. Reloading retries history without restarting the provider.

## Provider hydration

ACP `session/load` is the only source of history produced outside this process. While load is active,
`AcpAgentSession` collects the provider's `session/update` stream instead of publishing it live, so a
half-replayed conversation is never rendered. A successful load raises one host-internal `PaneSnapshot` event.

The collected stream must stay in conversation order, because the pane places a record where its stream first
appears and derives turn boundaries from where the prompts sit. Agent content is positioned by the delta it
streams; a replayed user prompt has neither that nor the local submission that places it live, so it is closed
— and published — the moment the replay moves past it, rather than at the end of the load.

On a cold load the pane is empty, so `AgentSessionHost` stores the snapshot and streams it as live records
inside the existing generation: connected clients receive the transcript without being told to re-sync, and a
client that has already loaded history keeps every ordinal it holds. Only a snapshot arriving over existing
content resets the generation and publishes `paneReset`.

```mermaid
sequenceDiagram
  participant Host as AgentSessionHost
  participant ACP as AcpAgentSession
  participant Web

  Web->>Host: lifecycle.sync
  Host-->>Web: bounded controls and attachments
  Web->>Host: HTTP agent history stream
  Host-->>Web: completion marker (pane not yet populated)

  ACP->>Host: PaneSnapshot after session/load
  Host->>Host: empty pane, so keep the generation
  Host-->>Web: live records
  Note over Web: transcript appears; no re-sync
```

## Transport isolation

The remote WebSocket transport lazily encodes oversized logical messages into bounded source chunks. Its send
loop round-robins active message routes while preserving FIFO order within each exact
`(scope, session, feature)` route. One connection admits one partial large body at a time while continuing to
serve small unrelated routes, bounding receiver reassembly memory without restoring head-of-line blocking. A
large agent record therefore cannot hold branch results, command responses, or another session behind all of its
chunks.

History uses HTTP streaming and does not enter this message outbox. Live updates retain the shared transport's
route fairness while history is in flight.

## Failure semantics

There is no rejected-session recovery fallback. If `session/load` or `session/resume` rejects the exact persisted
ACP session id, the native session fails visibly and retains its saved mapping for diagnosis. Starting a
different conversation is an explicit user action, never a silent transcript reset.

Weavie holds no second copy to fall back to, which is deliberate: a cached transcript shown after the provider
failed would render a dead session as a live one.
