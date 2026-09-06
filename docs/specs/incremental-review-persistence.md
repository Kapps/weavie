# Incremental review persistence

A checkpoint commits changed files, prompts, and review decisions in one SQLite
transaction. Editing one file does not clone or serialize other files' provenance,
scan their history heads, or rewrite their decision text.

## Ownership

`TrackedMap` and `TrackedSet` report changed keys from their mutation APIs. File
state has separate pending sets for history synchronization and persistence.
Synchronizing consumes the former; only a successful storage transaction clears
the latter. A failed save can therefore be retried without transporting history
twice or losing an earlier pending file change.

Provenance consists of immutable arrays and dictionaries. Transformations replace
the owning file's value. Snapshots can share it safely without cloning per-line
state, and a provenance-only edit cannot bypass change tracking.

Each history action owns immutable patch groups indexed by file path. Replacing a
group updates only that path's action index and pending storage record. History
heads contain only immutable text and existence values. Keep All and Revert All
remain single actions, but changing one constituent file does not serialize the
other files' patch groups.

## Storage and restoration

Each worktree owns `review/state.db` in its private workspace state directory.
`ReviewPersistence` applies upserts and deletions to a keyed record table within a
transaction. Records are:

- `metadata`: worktree identity, review source, and identity counters.
- `file:<canonical path>`: one tracked file's complete current review state.
- `history:<entry id>`: stack membership and small action metadata.
- `history-patches:<entry id>:file:<canonical path>`: one action's patches for one file.
- `prompt:<conversation id>`: that conversation's prompt.

Entry IDs also establish stack insertion order. They are independent of action
and patch IDs, because a multi-file undo can checkpoint while the source action
and its partially completed inverse both exist.

Startup loads and validates the committed records together, rejects orphan patch
groups or invalid identities, and reconciles only tracked paths with disk. It
never rewrites user files during restoration. The store uses SQLite transactions
and its rollback journal for atomicity; there is no asynchronous save queue,
debounce, custom replay log, or delayed durability window.

This format replaces the JSON review document. Older `state.json` documents are
not imported; no compatibility reader is maintained.

## Verification

Core regressions assert that a single-file edit writes only its file record and
changed metadata, regardless of unrelated files, prompts, or actions. A separate
Keep All regression asserts that transporting one file writes only that file's
patch group. Tests also cover dirty-state retention after save failure, every
intermediate multi-file undo checkpoint, and actual SQLite batch rollback/reopen.
The full-stack durable review scenario verifies keep/revert and undo/redo after
unload and a fresh host restart.

A local Release benchmark compared commit `961c6755` with this implementation.
Each file contained 500 lines of generated source text. After four warmup edits,
twenty alternating one-line edits each called `CaptureBaseline` and `RecordChange`.
A measuring persistence implementation counted serialized UTF-8 bytes without
disk I/O. Allocation counts used `GC.GetAllocatedBytesForCurrentThread`.

| Tracked files | Previous serialized bytes/edit | Incremental serialized bytes/edit | Previous allocated bytes/edit | Incremental allocated bytes/edit |
| --- | ---: | ---: | ---: | ---: |
| 1 | 256,688 | 256,390 | 1,585,116 | 1,179,340 |
| 50 | 12,804,590 | 256,390 | 27,544,262 | 1,179,454 |
| 200 | 51,216,790 | 256,390 | 107,017,490 | 1,179,274 |

These figures isolate checkpoint preparation and serialization, not end-to-end
agent latency or SQLite fsync time. Cost still depends on the changed file's size
and its relevant decision patches. Startup and explicit whole-review actions
legitimately inspect the whole review. The separate tracked-path deletion probe
on tool completion still checks file existence; it does not snapshot file text.
