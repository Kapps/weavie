# Persistent Reviews

Status: implemented
Last updated: 2026-07-22

A review belongs to its worktree, not to a live `HostSession`. Closing Weavie, restarting a remote worker,
unloading a worktree session, or moving between clients restores the same pending and kept hunks instead of
recomputing a fresh diff against `main` and losing the user's decisions.

This is persistence for the existing review engine, not a second diff viewer. `SessionChangeTracker` remains
the one canonical aggregate for ordinary agent changes, Diff Against, and PR reviews. Every mutation goes
through its transaction boundary; checkpoint encoding is only a projection of that state.

## Ownership and lifecycle

- One checkpoint document exists per `(workspace, worktree path)`, under
  `~/.weavie/workspaces/<workspace>/review-checkpoints/<worktree-digest>.json`.
- Unload, app close, worker restart, and client reconnect preserve it. Successful worktree deletion removes it.
- PR/ref identity is tracker state, so a restored `turn-changes` message retains `PR #42` or `vs main` without
  a second host-side association map.
- PR comments are not checkpointed. They are a re-fetchable host cache fenced by the live session instance,
  worktree, arm token, PR, and head SHA; each refresh also carries a request sequence and review-lifetime
  cancellation token so a stale unload/re-arm completion cannot overwrite the replacement session.
- Review decisions restore, but undo/redo stacks start empty. An undo or redo that already ran is ordinary
  canonical state and is checkpointed; stale action mementos are deliberately not replayed into a new process.

## Checkpoint shape

The current saved file is the reconstruction anchor. Its content is **not** stored. Each file records whether
that anchor exists, its SHA-256 hash over the exact tracker text, and sparse character splices that reconstruct
the four internal versions from it:

- `current`: agent-attributed review text (which can omit unrelated saved user edits);
- `reviewBaseline`: pending/kept boundary;
- `acceptedAnchor`: bright/faded boundary;
- `sessionBaseline`: the session-wide change baseline.

A representative document looks like this (hashes shortened here only for readability):

```json
{
  "version": 1,
  "review": {
    "prNumber": 0,
    "label": "vs main",
    "headRef": "",
    "mergeBase": "9f8c…",
    "headSha": "a431…",
    "repo": null,
    "worktree": "/worktrees/feature"
  },
  "armToken": 7,
  "activeReviewToken": 7,
  "nextOriginId": 12,
  "files": [
    {
      "path": "src/example.cs",
      "diskExists": true,
      "diskHash": "b5d4…",
      "createdSinceBaseline": false,
      "current": { "hash": "b5d4…", "splices": [] },
      "reviewBaseline": {
        "hash": "017a…",
        "splices": [
          { "offset": 84, "deleteLength": 11, "insertText": "oldValue" }
        ]
      },
      "acceptedAnchor": {
        "hash": "017a…",
        "splices": [
          { "offset": 84, "deleteLength": 11, "insertText": "oldValue" }
        ]
      },
      "sessionBaseline": {
        "hash": "017a…",
        "splices": [
          { "offset": 84, "deleteLength": 11, "insertText": "oldValue" }
        ]
      },
      "provenance": {
        "origins": [
          { "id": 12, "pending": true, "prompt": "Update the parser" }
        ],
        "runs": [
          { "start": 4, "length": 1, "origin": 0 }
        ],
        "deletedGaps": []
      }
    }
  ],
  "guards": []
}
```

Unchanged regions never appear in the document. A wholesale replacement can naturally make a splice large,
but the format never stores an unconditional copy of the disk file. Splices use raw character offsets and
retain CRLF/CR/LF exactly; hashes do not normalize whitespace or casing.

A completed disk-changing action can leave a clean file referenced only by the live undo/redo history. Such a
path gets a guard containing only its relative path, existence, and disk hash. The history itself is not saved;
the guard makes any later disk transition detectable if its resulting checkpoint cannot be written, and is
discarded on the next hydration.
If a guarded path becomes unreadable or non-text while the session is live, the tracker invalidates the
undo/redo actions that touch it, replaces the stale checkpoint without that guard, and surfaces a keyed review
problem. Unrelated review actions continue normally.

## Restore and invalidation

For each recorded path, hydration reads the current disk text and requires an exact match on both existence
and hash. A match reconstructs and integrity-checks all four versions, then restores compact provenance runs
and the monotonic origin counter. A mismatch invalidates only that file; other files still restore. If every
file in an armed review is invalid, its PR/ref identity is dropped so its label cannot attach to later, unrelated
changes.

There is no fuzzy hunk relocation and no automatic re-diff against a branch. A file changed while Weavie was
closed is genuinely a different anchor, so Weavie reports the invalidation visibly and leaves it alone.

Typing during review does not cause false invalidation. The editor's save path is:

1. write the complete working copy to disk;
2. reply to the editor with the typed write result;
3. synchronously publish the session's `FileSaved(path, content)` event;
4. let the tracker rebase author provenance and replace the checkpoint against that saved anchor.

There is no keypress listener or save-like debounce. Monaco already owns save debouncing; only a successful
save emits this event.

## Transaction and failure rules

Every tracker mutator crosses one `MutateReview` commit boundary: agent change capture, editor save, keep,
revert, un-keep, keep-all, turn-boundary commit, undo/redo, deletion reconciliation, and atomic PR/ref arm or
retract. `Changed` remains a rendering event and is not the persistence mechanism.

Each mutation reports whether durable state actually changed and which paths it changed. A no-op hook or failed
review guard does not project a checkpoint. For an actual mutation, the tracker derives the next atomic document
from the canonical aggregate plus a cache of the last successfully saved per-file encodings: only dirty or newly
visible files recompute their hashes and four sparse diffs. Desired visible and history-guard membership is still
recomputed from canonical state, so the cache is disposable output rather than another source of truth. Candidate
encodings are promoted only after the store succeeds (or produces the same document); a failed disk-changing
write keeps its dirty paths pending for the next commit attempt.

History guards retain the hash captured when they entered the checkpoint. Cheap file metadata determines whether
an existing guard needs another readability check; an unrelated mutation never blesses an out-of-band edit by
moving the guard hash forward.

- Disk-neutral actions snapshot the aggregate first. If atomic checkpoint replacement fails, tracker state rolls
  back and the user sees that the action was not applied.
- When the source file already changed (agent edit, editor save, revert), memory retains the real post-write
  state and reports the checkpoint failure. The older checkpoint cannot be replayed silently because its file
  hash or history-only disk guard no longer matches.
- Multi-file disk actions report each path to the transaction boundary only after both its disk write and
  canonical memory update finish. If a later path fails, the boundary checkpoints the successfully applied
  prefix before rethrowing the original operation error. A simultaneous checkpoint failure cannot hide that
  error: the applied paths remain pending for the next commit attempt and a review problem is surfaced.
- Ref/PR arming reserves a monotonic token before asynchronous git/forge work. It validates every seed against
  disk, then replaces the board, identity, and all seeds in one checkpoint. A stale arm cannot overwrite a newer
  one, and a partial seed loop is never durable.
- Restore, invalidation, and save failures are retained as tracker problems and replayed as keyed notifications
  whenever the session's editor projection mounts.

The checkpoint is written through a unique sibling temporary file and atomic replacement under an owner-only
directory/file on POSIX. A crash therefore leaves either the prior complete document or the next complete
document, never a torn one.

## Tests

- `SessionChangeTrackerPersistenceTests`: exact round trips, mixed line endings, sparse encoding, kept/pending
  triples, saved user edits, per-file invalidation, created files, provenance continuity, atomic arm,
  persistence-failure rollback/retry, and incremental projection reuse across no-op hooks, unrelated files, and
  unchanged guards.
- `PersistentReviewTests`: real host restart and unload/reload journeys, including a saved user edit that survives
  a later reject and a kept hunk that remains kept.
- `ReviewCheckpointStoreTests`: atomic replacement and owner-only file permissions.
