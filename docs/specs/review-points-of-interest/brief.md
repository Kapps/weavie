# Step through comments and hints

Brief for [#1162](https://github.com/Kapps/weavie/issues/1162). Builds on [pr-comments.md](../pr-comments.md) and
[inline-review-hints.md](../inline-review-hints.md). [design.md](design.md) has the design.

## Goal

A user working through review feedback steps from one PR thread or agent hint to the next, in diff order and across
files, and always sees how many are left. Feedback becomes a checklist instead of a search through the diff.

## Behavior

### The sequence

- One per session: its PR review threads and its review hints together, including the viewer's own draft comments.
- An item leaves the sequence only through what Weavie already knows: a hint is dismissed, or GitHub reports a thread
  resolved or outdated.
- Order: the files of the session's review in the review's own order, then every other file by path; within a file
  by the line the item was made on.
- The total never depends on which files are open or scrolled into view.

### Stepping

- **Next Comment or Hint** / **Previous Comment or Hint**, on `$mod+alt+]` / `$mod+alt+[`. They replace
  `weavie.pullRequest.nextComment` / `prevComment` and `weavie.reviewHints.next` / `previous`, which are deleted.
- Available whenever the session has a thread or a hint, from the unified review, a single-file review, and a plain
  editor tab. The chord needs focus in the editor pane, outside a comment box.
- A step stays in the kind of view it started from: in the unified review it scrolls there, expanding a collapsed
  file; from a file tab it opens the item's file in a tab.
- Focus lands in the editor on the item's line, so the same key steps again. A thread shows an accent outline; a
  hint opens its popover.
- **Where a step starts.** From the current item, which is the outlined thread or the open hint. Escape, or a click
  elsewhere in the code, clears it; the next step then starts from the cursor. Next on the last item wraps to the
  first.

### What the user sees

- **Review header** (unified review): a comment icon, `‹ 3 of 8 ›`, and a filter button, left of the review actions,
  styled as the existing per-file `‹ 1 / 4 ›` stepper. With no current item it shows the total alone; with nothing
  to step through, `0` and disabled arrows. A session with no pull request and no hints shows no stepper.
- **The header's actions are icons** in one row, at the top of the review and scrolled alike: the stepper and
  filter, Undo rejection, Revert pending, Keep all, and once scrolled, Review with its pending count. Everything in
  the header is centred on one line.
- **On the current item**, in every surface: `‹ 3 of 8 ›` in the thread card's action corner, and the count between
  the hint popover's existing arrows.
- **Filter menu**, three rows with counts, all remembered across sessions and restarts:
  - **Comments** and **Hints** are the existing show/hide switches (`weavie.pullRequest.toggleComments`,
    `weavie.reviewHints.toggle`). Off hides that kind in every editor and drops it from the sequence.
  - **Resolved and outdated**, off by default, puts resolved and outdated threads back in the sequence. It changes
    stepping only.
- A thread resolved on GitHub renders collapsed with a Resolved badge.
- Every control's tooltip is its command name plus the effective shortcut.

## Decisions

- **One stepper, not one per kind.** The filter covers "only hints" and "only comments".
- **One switch per kind.** The filter and the hide toggles are the same state, so nothing is hidden but counted.
- **The app does it, from the two lists it already has.** No host placement. A design where the host tracked each
  item's current line, so that editing an item's line dropped it from the count automatically, was costed at about
  three times the code for that one behavior and scrapped.
- **Dismiss is how a hint is ticked off**, including after a fix. The existing fade on an edited line stays a visual
  cue only.
- **Resolved is read, never written.** Resolving from Weavie waits for a later iteration.
- **Header and on-item position together.** Single-file review and plain tabs have no review header.
- **Not in the floating Keep/Revert bar.** It is already full and wraps (`temp/poi-mockup/7-toolbar-wraps.png`).

## Accepted limits

- A step can land a few lines from its item in a file with local edits, until that file's editor has loaded and
  corrects the position. Order within such a file can be off by the same amount.
- The count does not fall when something is fixed, only when it is dismissed, resolved, or outdated.
- A thread's expanded state still resets when it scrolls out of view.

## Why the current shortcut feels broken

`$mod+alt+]` today steps within one file only. In the unified review every file is its own editor, so it stops at
each file's last thread with "No more PR comments below in this file", and it needs the cursor in a file that is
part of the pull request. The session-wide stepper removes both limits.

The chord does not fire while the git-blame popover holds focus, which is that popover's design.

## Out of scope

- Resolving or unresolving threads from Weavie.
- A Weavie-only "handled" mark, and local notes.
- Dropping an item automatically when its line is edited.
- The compact (mobile) layout.
- PR-level conversation comments, which have no line.

## Mockups

`temp/poi-mockup/` (gitignored; `run.sh` re-renders them in the real app). Shots 0 to 6 show the agreed look; the
faded hint in shot 4 is no longer excluded from the count.
