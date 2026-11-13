# Challenge: step through comments and hints

Argues against the approach in [brief.md](brief.md). Objections are ordered strongest first; each rests on the
code cited. The `weavie-architect` agent reviewed the brief independently and reached the same verdict; its
findings are merged in.

**Verdict.** The behavior is sound, and the web client is the right owner of the sequence, the current item and
reveal. It is the wrong owner of what defines the sequence: which hints are settled, and where each item sits.
Neither is in "the two existing stores", so the approach cannot be built as written. Moving those two facts to the
host is materially better (see [Alternatives](#alternatives)).

## 1. The sequence cannot be derived from the two stores

What the user would see: the total changes as files scroll into view, or the header counts a hint the editor shows
as faded. The brief lists this as an edge case; it is the core of the design, and the approach gives it no owner.

- **Settled is computed only inside a mounted editor.** A hint is "changed" when a diff of its captured text
  against the live Monaco buffer says its line was touched (`src/web/src/editor/review-hints/hint-anchor.ts:21`,
  `:24`), and that diff runs only in the per-editor controller, over `model.getValue()`
  (`src/web/src/editor/review-hints/review-hint-controller.tsx:78`). The store carries only `line`, `sourceHash`
  and `quote` (`src/web/src/editor/review-hints/review-hints-store.ts:10`).
- **Most files have no mounted editor.** The unified review renders sections through a virtualizer with an overscan
  of one (`src/web/src/editor/review/UnifiedReview.tsx:139`, `:217`); a plain tab mounts one file.
- **Deciding it for the rest needs a second pipeline.** Per hint: its captured source, one request per
  `(path, hash)` returning a whole file (`src/web/src/editor/review-hints/review-hints-store.ts:80`,
  `src/Weavie.Hosting/HostCore.ReviewHints.cs:17`); the current text of every hinted file, including ones never
  opened; and re-running on each change to them. Hints accumulate until dismissed
  (`src/Weavie.Core/Review/Hints/ReviewHintStore.cs:10`), so the set is unbounded.
- **The web holds no current text for unopened files.** The review cache keeps baselines by revision, to render
  against a live model (`src/web/src/editor/review/review-texts.ts:5`). The only other reads are Monaco's file
  provider (`src/web/src/editor/host-file-provider.ts:103`) and text-by-revision
  (`src/web/src/editor/editor-controller.ts:276`). A session-wide count would need a new text cache with its own
  invalidation, and a Monaco diff on the UI thread per file (one-second budget each,
  `src/web/src/editor/review/diff-computation.ts:5`) to draw a number in a header.
- **The main way a hint settles is the one the web cannot see.** Fix sends a prompt
  (`src/Weavie.Hosting/HostCore.ReviewHints.cs:30`) and the agent edits the file on disk, usually one with no
  mounted editor. The host learns of that edit first, through the session's file activity.
- **Two pipelines give two answers.** A mounted file would be judged against its buffer and an unmounted one
  against something else, so the count moves when a section mounts — and "the header, the on-item position, and both
  commands … cannot disagree" stops being true for the fade the user is looking at.
- **Some files are undecidable.** A diff that hits its time limit throws (`src/web/src/editor/line-anchor.ts:29`) and
  the controller places nothing (`src/web/src/editor/review-hints/review-hint-controller.tsx:85`). A hint whose file
  was deleted never gets an editor at all; existence is checked only at capture
  (`src/Weavie.Core/Review/Hints/ReviewHintStore.cs:140`).

**Order has the same hole.** "Within a file by line" names four coordinate systems: a hint's line is numbered
against its captured text; a right-side thread against the PR head; a left-side thread against the merge-base
(`src/web/src/editor/pr-comments/anchor-map.ts:34`, `:46`); the cursor against the buffer. Outdated threads all carry
line 0 (`src/Weavie.Core/Review/GitHubReviewProvider.GraphQl.cs:107`). Mapping a thread onto the buffer needs that
file's head and base texts, requested only by a mounted binding
(`src/web/src/editor/pr-comments/pr-comment-controller.tsx:78`). So the client must either sort by raw numbers
(stable, but a hint and a thread on neighbouring lines can step in the wrong order after local edits) or by placed
lines (correct, but different before and after a file mounts). Today's hint stepper already shows the seam: it uses
the placed line for the active editor and the raw captured line for everything else
(`src/web/src/editor/review-hints/review-hint-commands.ts:66`).

## 2. "Where am I" has two owners

What the user would see: the header says `3 of 8` while the cursor sits somewhere else, and Next jumps from a place
the header does not show.

The approach says the current item is "not inferred from the cursor". The behavior says "from a cursor that isn't on
an item, Next goes to the first item after it". Both cannot own the step's origin, and the brief never says what
moving the cursor does to the position.

- **"The cursor" is not well defined.** A session's cursor is the active tab's last-activated editor
  (`src/web/src/editor/editor-context.ts:88`); in the unified review that is whichever section last took focus
  (`src/web/src/editor/review/review-editor.ts:263`), and it disappears from the registry when the virtualizer
  unmounts that section (`src/web/src/editor/editor-context.ts:100`).
- **A third pointer already exists.** The open hint popover is one-per-session state
  (`src/web/src/editor/review-hints/review-hints-store.ts:47`) and is cleared when its editor is disposed
  (`src/web/src/editor/review-hints/review-hint-controller.tsx:263`), which in the unified review means scrolled out
  of range. The brief does not say whether closing the popover, or scrolling it away, clears the current item. A
  hover popover and the current item would be two selections unless the open hint *becomes* the current item.
- **Today's stepper already has the stale-origin bug.** It steps from the open hint before it considers the cursor
  (`src/web/src/editor/review-hints/review-hint-commands.ts:70`), and only a mouse click clears the open hint
  (`src/web/src/editor/review-hints/review-hint-controller.tsx:182`). Arrow away, press Next, and it continues from
  the hint left behind.
- **Clearing the position on cursor movement** means listening to every editor's cursor, a per-keystroke path that
  must pass `pnpm e2e:perf`.

It needs one rule, stated in the brief. The simplest: the origin is the current item; any cursor move the reveal
did not cause clears it; with none, the origin is the active tab's captured location, which every surface already
reports — the focused section's cursor, else the visible file's line
(`src/web/src/editor/review/review-surface.ts:180`). "Continues from where it was" then needs no extra state.

## 3. Hints and threads stay two systems under a third

The brief unifies the *list* but leaves reveal, focus and per-item view state in two unrelated controllers. "Reveal
through what exists" covers only scrolling to a line.

- **Threads have no cross-file reveal.** `navigate` moves the cursor inside one editor binding
  (`src/web/src/editor/pr-comments/pr-comment-controller.tsx:291`) and is reached through the active editor
  (`src/web/src/editor/editor-command-bindings.ts:109`).
- **Hints have an ad hoc one.** Navigation opens a hint with no owner, and the first binding that has placed it and
  has client rects claims it (`src/web/src/editor/review-hints/review-hint-controller.tsx:190`), behind a three-way
  fallthrough in the command (`src/web/src/editor/review-hints/review-hint-commands.ts:83`).
- **A thread's expanded state is private to one binding** and dies with it
  (`src/web/src/editor/pr-comments/pr-comment-controller.tsx:143`, `:318`). A step cannot expand a thread in a
  section that is not mounted yet, the same thread in a plain tab and in the review has two states, and "resolved
  renders collapsed" resets the user's expansion every time the section scrolls out and back.
- **The unified reveal restores a text location and focuses the section**
  (`src/web/src/editor/review/review-surface.ts:150`). The accent outline, the expansion and the popover need a
  second, item-specific step that today exists only for hints.

The missing abstraction is a session-owned **review item**: one identity, one placement, one current/expanded
state, one claim path, rendered by whichever binding shows its file. The code already leans that way — `FixItem`
(`src/web/src/editor/FixPrompt.tsx:9`), the shared `line-anchor.ts`, and the hint controller taking the PR
controller as a dependency (`src/web/src/editor/review-hints/review-hint-controller.tsx:40`).

## 4. "Settled" means different things for the two kinds

- **The same signal settles one kind and not the other.** A hint whose line was edited leaves the sequence. A thread
  whose line was edited gets a "Changed locally" note from the identical test
  (`src/web/src/editor/pr-comments/anchor-map.ts:36`) and stays.
- **Threads cannot be ticked off in Weavie.** Resolving and a local "handled" mark are both out of scope, and forge
  state refreshes once a minute while visible (`src/web/src/editor/pr-comments/pr-comment-refresh.ts:8`). For a PR
  author working through review comments, the count does not move as they work. "Feedback becomes a checklist"
  holds for hints only.
- **The hint rule is a heuristic.** A fix that adds a guard on the line above leaves the hint open; a formatter that
  rewraps the line closes it; undo reopens it (`src/web/src/editor/line-anchor.ts:20`).

This rule is also what makes objection 1 expensive. If hints left the sequence only by Dismiss, the count would be
derivable from the store alone.

## 5. The filter is a third copy of visibility

The brief adds a persisted, all-sessions filter beside two existing switches that are page-global and not persisted
(`src/web/src/editor/pr-comments/pr-comments-store.ts:86`,
`src/web/src/editor/review-hints/review-hints-store.ts:30`), then adds "stepping turns visibility back on" to
reconcile them. That leaves states with no defined meaning: hints hidden but counted, or visible but skipped by the
stepper and shown with no position. One concept should have one store: either the sequence is "filter ∩ visible"
and stepping never changes visibility, or the toggles become the filter. The filter menu is also UI over a setting,
which the settings rule reserves for "no UI beyond the settings registry"; the brief should say why this is the
exception.

## 6. The shortcut bug is scheduled with no cause

The brief commits to fixing "`editorFocused` stays false after the palette closes" inside this work while stating
the cause is unknown. The key is a cache of `document.activeElement` maintained from `focusin` events
(`src/web/src/App.tsx:1886`, `src/web/src/commands/context.ts:37`) and read at dispatch
(`src/web/src/commands/keybindings.ts:144`); the palette restores focus by hand
(`src/web/src/chrome/Omnibar.tsx:361`). The defect is in that owner and affects every editor-gated shortcut, so it is
a separate change with its own test, landing first. Until the cause is known, the brief cannot rely on "focus stays
in the editor so the same key steps again", which reads that key. The new commands also need a stated `when`: today's
binding is gated on `prCommentable && editorFocused` (`src/Weavie.Core/Commands/CoreCommands.cs:1900`), and
`prCommentable` follows the active editor (`src/web/src/editor/pr-comments/pr-comment-controller.tsx:174`), which a
session-wide stepper must not.

## 7. Smaller gaps

- **"The open review" needs a definition.** The review is session state that exists while parked
  (`src/web/src/editor/editor-controller.ts:1659`), not a tab. Tying order to a tab being open would renumber
  positions when a tab closes.
- **Items with no file.** A hint on a deleted file, or a thread on a path absent from the worktree, cannot be
  "opened in a plain tab". The brief gives them no state.
- **Tests.** The fake PR scenario seeds one thread per PR (`src/web/e2e/harness/git-workspace.ts:263`); it needs
  left-side, outdated and resolved threads, not only "more than one".

## Alternatives

| | Owner of settled and order | Gains | Costs |
| --- | --- | --- | --- |
| **A. Brief** | Web, per page | Smallest host change. Judged against the unsaved buffer where a file is mounted. | A second placement pipeline for unmounted files, or a count that moves with scrolling (objection 1). |
| **B. Host settles and orders** | Core, per session | Count and order are decidable for every file, including ones an agent edited unseen, and identical on every client. The web count is a sort and a filter of pushed data. Agents can read what is left. Matches `pending`, which the web is told never to re-derive (`src/web/src/editor/review/review-store.ts:68`). | A host line mapper: Core has line alignment (`src/Weavie.Core/Changes/LineDiff.cs:14`) but no line map, and it can differ from Monaco's diff on repeated lines and on empty text (`src/Weavie.Core/Changes/LineDiff.cs:48`). Judged against saved text, so the fade trails an unsaved edit until its save. More host work than the brief plans. |
| **C. Host owns all placement** | Core; the web draws host lines | One diff engine for hints; removes the web's hint diff and its `source` request. | Unsaved buffers need revision matching. Threads cannot follow: posting maps a buffer line back to the PR head in the web (`src/web/src/editor/pr-comments/anchor-map.ts:56`). |
| **D. Explicit settle** | Web, from the stores alone | No placement needed for the count. One rule for both kinds. Total is stable by construction. | The user dismisses each hint after fixing it. Reverses a decision in the brief. Order still needs a mount-independent key (objection 1). |

**B in detail.** `ReviewHintStore` already holds each hint's captured text and reads only the reported file
(`src/Weavie.Core/Review/Hints/ReviewHintStore.cs:119`, `:11`). It re-places a hint when the session file-activity
stream reports a save or change on that hint's path — driven by hinted paths, never a workspace walk, as the review
presentation already does per path (`src/Weavie.Hosting/HostCore.FileActivity.cs:44`) — and publishes `line` and
`changed` with each hint. `PullRequestComments` already reads head and merge-base text per path
(`src/Weavie.Core/Review/PullRequestComments.cs:107`) and can publish each thread's worktree line the same way. The
web sequence is then a pure function of two published lists, the review's file order and the filter; it owns only
the current item and reveal. Editors keep a buffer diff solely to draw an item on the right line while typing, and
take the faded state from the host's flag, so exactly one side decides "changed".

## Recommendation

1. Move settled and line to the host (B). It is the only option where the brief's own edge case holds by
   construction.
2. Introduce the session-owned review item (objection 3) and put the current item, expansion and claim there; delete
   `navigate`, the ownerless-hint claim and the per-binding collapse map.
3. State one origin rule (objection 2) and one visibility rule (objection 5) in the brief.
4. Decide whether a locally edited thread is settled, so both kinds follow one rule (objection 4).
5. Root-cause the shortcut bug first, as its own change (objection 6).

## Not verified

- What the host's watcher pushes to the web carry. If the web already receives a path invalidation for every file,
  option A costs less than stated; the ownership argument stands.
- The autosave delay, which bounds how long the host's flag trails the buffer in option B.
- Whether several pages can attach to one session at once.
- The cost of the host's line alignment on large files.
- The cause of the shortcut bug.
- How a thread on a file deleted by the PR renders in the unified review today.
