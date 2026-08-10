# Native agent pane persistence

The native structured-agent pane renders provider-neutral `AgentPaneMessage` records owned by
`AgentSessionHost`. The transcript survives session unloads and worker restarts without making session sync or
the shared message transport proportional to the transcript's size.

## Ownership

Each worktree has an owner-only transcript at
`~/.weavie/workspaces/<id>/agent-panes/<worktreeDigest>.json`
(`WeaviePaths.WorkspaceAgentPaneFile`). `AgentPaneTranscriptStore` persists the durable message subset as
append-only JSONL and reports I/O failures through its `Log` event.

`AgentSessionHost` is the single transcript owner. It maintains the materialized in-memory pane, journals live
messages, seeds persisted history on its journal worker, and replaces both stores when the provider supplies an
authoritative resumed-thread snapshot. The replacement increments a generation and publishes `paneReset`; it
does not replay the replacement through the live-message path.

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

The journal readiness task is awaited by `historyPage`, not session sync. A newly loaded session can therefore
answer unrelated commands while its transcript is still being read from disk.

## Provider hydration

Codex `thread/resume` is authoritative for history produced outside Weavie. Hydration raises one host-internal
`PaneSnapshot` event. `AgentSessionHost` atomically replaces the in-memory transcript and journal, increments the
generation, and tells connected client session owners to restart paging. This avoids broadcasting every hydrated
item and avoids temporarily exposing the persisted seed as a second copy of the same conversation.

```mermaid
sequenceDiagram
  participant Store as AgentPaneTranscriptStore
  participant Host as AgentSessionHost
  participant Codex as CodexAppServerSession
  participant Web

  Host->>Store: read persisted durable history
  Web->>Host: lifecycle.sync
  Host-->>Web: bounded controls and attachments
  Web->>Host: agent.historyPage(cursor = null)
  Host->>Host: await journal readiness
  Host-->>Web: newest page + fixed cursor
  loop until cursor is null
    Web->>Host: agent.historyPage(cursor)
    Host-->>Web: older page + cursor
  end

  Codex->>Host: authoritative PaneSnapshot
  Host->>Store: replace durable history
  Host-->>Web: paneReset
  Web->>Host: agent.historyPage(cursor = null)
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

## What persists

`AgentPaneTranscriptStore.IsPersistable` keeps only durable conversation:

- **Persisted:** `user-message`, `user-steer`, submitted `user-image`, `item-completed`, and `interrupted`.
- **Live only:** turn lifecycle, in-progress items, streaming deltas, incremental diffs, prompts, drafts,
  edit locations, thread readiness, and transient launch/stderr warnings and errors.

JSONL append keeps the unbounded transcript off an O(n²) whole-file rewrite path. Loading is line-resilient: a
torn final record is skipped and earlier records remain available. The transcript is deliberately uncapped.

## Reset

`transcript-reset` clears the in-memory pane and deletes the journal when Codex starts a genuinely fresh thread,
including recovery after a saved thread is rejected. An authoritative resumed-thread snapshot uses the atomic
replacement path described above. A resume rejection that also prevents the replacement thread from starting
surfaces as an error and leaves the saved mapping and transcript intact.
