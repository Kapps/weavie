# Review state

How a session's review decides which files it lists and what each is compared against. The review
surface, hunks, and actions are in [turn-review.md](turn-review.md); the entry points are in
[diff-against.md](diff-against.md) and [open-pr.md](open-pr.md). Where they disagree, this spec wins.

## Invariants

1. **One review per session, one "before".** Every held file has three texts, each with existence:
   *before*, *kept* (starts equal to before; only an explicit Keep advances it), and *current*.
2. **A git review's list and befores are git's answer.** Starting one replaces the review state. Nothing
   the tracker remembered is trusted, and a file outside git's list is not in the review.
3. **A file whose current equals its before (text and existence) is not listed**, unless it carries a
   rejection that can still be undone. It stays held, with its undo history, until the review is replaced
   or closed.
4. **External changes are never diffed in bulk against a stored before.** A pull, rebase, merge or shell
   change makes the review re-resolve its base and ask git again.
5. **Closing scraps.** It approves nothing and remembers nothing; opening the same diff again starts over.
6. **Agent line provenance is not review state.** It survives a replace and a close, so corrections over
   agent lines are still captured after either.

## Modes

| Mode | Before | Current | Joins |
|---|---|---|---|
| Default (no review started) | pre-edit capture | the agent's changes | agent edits |
| Git review (branch base, PR, `HEAD`, a ref) | git content at the resolved base | disk | agent edits, editor saves, and changes the watcher sees, one file at a time |

A PR review compares the working tree
with the merge-base of the PR's base branch; list and content come from the same two endpoints.

## Lifecycle

```mermaid
stateDiagram-v2
    [*] --> Default
    Default --> Git: start a review
    Git --> Git: start a review / base moved (replace + carry-over)
    Git --> Git: a file changed (join that file)
    Git --> Default: close / branch checkout / empty list (scrap)
    Default --> Default: agent edit (capture) / keep the last change (scrap)
```

### Start and re-list: replace with carry-over

Starting a review, clicking the chip of the review already open, and every re-list run one pipeline:
resolve the base, ask git for the changed paths (tracked diff plus untracked files), read each before at
the base and each current from disk, then replace the review state in one step. An empty list closes the
review with a notice.

A path keeps its kept text and its undo history iff its before in the new state equals its before in the
old state, in text and existence. Every other path starts with kept equal to before. Paths missing from
git's list are forgotten, along with every undo step that touched them.

### Join: one known path

While a git review is open, a completed agent edit, an editor save, or a known deletion updates that one
path. A path the review does not hold yet gets its before from git at the review's base, in one read. A
held path only refreshes current from disk, with no git call. A join that lands while a re-list is reading
git is recorded on that re-list and applied again after it.

### Watcher changes and re-lists

A file the watcher saw change that the review doesn't show as it is on disk is queued. The review checks its
base once for the whole queue: unmoved, the queued files join one by one as above, with one git read for any it
doesn't hold yet; moved, the review is listed again from git, which settles the queue. Files join only if HEAD
hasn't moved since the base was checked; a file stays queued until it has joined, and a join that fails hands the
queue to a full listing. A changed `.gitignore` lists the review in full, since it changes which files belong. A listing from git in full
happens only when a review starts, its base moves, or a saved review loads after a restart, when nothing watched.

| Trigger | Action |
|---|---|
| Watcher change the review doesn't show | queue the files; join them once the base is checked unmoved |
| Git metadata change (HEAD, index, refs) | re-resolve; re-list when the base moved |
| Rebase, `git am`, bisect or conflicted merge stopped mid-way | wait until it finishes |
| Index lock held | wait; its release (even with no index change) looks again |
| Branch checkout, or the target no longer exists | close |

A path the review can't hold (ignored, outside the worktree) is remembered until the next listing, so its saves
don't ask git again. Binary files are left out of the listing, as an agent's binary edit is.

The watcher also echoes Weavie's own saves and agent edits: a path is an echo when the tracker holds it and
disk equals its current, and an echo does nothing. The base is resolved when the queue is handled, never from
the trigger.

### Keep and revert

Keep, Keep File and Keep All advance kept and are undoable. In a git review they keep what the review shows; a
change it hasn't listed yet stays pending. Revert File and Revert All refuse while a file on disk differs from
what the review shows, so they never overwrite a change the user hasn't seen. Keeping the last pending change of a git
review leaves it open with every change kept. In the default mode it scraps the review, as an accepted
review has nothing left to show.

### Close

Closing drops every review entry, both undo stacks, and the review identity. Agent provenance with lines
still pending stays, so later edits over them are still corrections. An agent edit already under way when a
review closes keeps the before it captured when it started, so a file it changes never reads as created.

## Cost

| Event, review open | Git processes |
|---|---|
| Agent edit or save, path held | 0 |
| Agent edit or save, path not held | 1 |
| Echoed watcher invalidation | 0 |
| External change batch, base unmoved | 3 to check the base, plus 1 for files not held yet |
| External change batch, base moved | one listing from git |
| No review open | 0 |

Keep, Revert and undo reconcile only the paths they act on. Agent tool completion does no per-file work
over a git review's list. The budgets are pinned in `GitOperationBudgetTests`.

## Persistence

`review.db` stores the review identity, each held path's texts, undo history, and provenance for files
with pending agent lines. A restored git review is not reconciled against disk; it is re-listed, so keeps
survive a restart where the before is unchanged. A restored default review reconciles its held paths.

## Not covered

- Kept changes drawn as a normal diff with pending ones emphasised (follow-up; today kept ones fade).
- Remembering kept hunks across reviews by content.
- Files git cannot see (ignored files, scratch roots): default mode only.
- Binary files, symlinks, submodules and mode-only changes keep today's handling.
- Befores are raw blobs: a joined file under line-ending conversion or LFS can list with no changed lines
  (`cat-file --batch --filters` fails with "missing path" on git 2.47).
