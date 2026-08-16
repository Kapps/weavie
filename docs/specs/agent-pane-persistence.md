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

## Pull-paged history

`lifecycle.sync` publishes bounded interactive state only. It never pushes transcript history.

The client-side session owner requests `agent.historyPage` when the exact session is created from the host
catalog. Selection only chooses where that already-owned state is rendered. Pages are returned newest first,
with a target serialized size of 192 KiB. A record larger than one page is serialized once and split on UTF-16
character boundaries. Every fragment identifies the record and declares its `jsonOffset` and total
`jsonLength`, so every unbounded record field remains pageable. Each first request captures an immutable,
peer-owned read of the materialized transcript. A cursor carries three values for that read:

- `readId` addresses the peer's exact immutable read.
- `before` advances toward the start of the fixed transcript.
- `jsonBefore` advances through one oversized record without changing its ordinal.

Live mutations never invalidate an active read: every page comes from the captured record revisions, and a later
read observes the newer state. Only the currently fragmented serialized record is retained between requests;
ordinary record JSON is released with its page. Completing, removing the client session, replacing the read, or
disconnecting the peer releases the read and its fragment cache.

Every wire record carries its generation, stable ordinal, and per-mutation revision. The web accumulator
reassembles serialized-record fragments, validates their identity, selects the newest revision for each ordinal
across history and live traffic, and rejects stale generations. It publishes the newest complete page promptly,
accumulates older pages without repeatedly rebuilding the growing transcript, and publishes the full transcript
once at completion. Fragment-only pages do not publish unchanged state. It yields one render frame before every
next request, so paging cannot monopolize the browser main thread. Removing the exact client session aborts its
pull; reconnect starts a fresh immutable read to recover messages emitted while the page was offline. A client
that completed its prior read supplies the generation and global mutation revision as a baseline; the host sends
only records changed after that revision, or an empty page when the transcript is unchanged. A generation change
returns the complete replacement.

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
client that has already paged history keeps every ordinal it holds. Only a snapshot arriving over existing
content resets the generation and publishes `paneReset`.

```mermaid
sequenceDiagram
  participant Host as AgentSessionHost
  participant ACP as AcpAgentSession
  participant Web

  Web->>Host: lifecycle.sync
  Host-->>Web: bounded controls and attachments
  Web->>Host: agent.historyPage(cursor = null)
  Host-->>Web: empty page (pane not yet populated)

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

This transport fairness is defense in depth. Paging is what prevents transcript loading itself from becoming one
unbounded logical operation.

## Failure semantics

There is no rejected-session recovery fallback. If `session/load` or `session/resume` rejects the exact persisted
ACP session id, the native session fails visibly and retains its saved mapping for diagnosis. Starting a
different conversation is an explicit user action, never a silent transcript reset.

Weavie holds no second copy to fall back to, which is deliberate: a cached transcript shown after the provider
failed would render a dead session as a live one.
