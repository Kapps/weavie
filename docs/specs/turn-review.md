# Reviewing auto-applied changes (post-turn review)

Status: in progress
Last updated: 2026-06-24

A keyboard-first flow for reviewing what Claude changed during an autonomous turn in an **auto-apply
mode** (`acceptEdits` / `bypassPermissions`). Claude runs a full turn without stopping to ask; when it
finishes you walk the result inline, in the live editor, change-by-change and file-by-file. Doing
nothing keeps the change — it was already written to disk.

There is **no separate review panel and no "show changes" list.** The review surface is the
**hovering inline-diff toolbar** that already renders over the editor; review is the changes themselves,
decorated in the files where they live, walked from that one toolbar. This builds directly on the
hook-driven change tracker and the inline diff renderer that already exist. See
[permission-modes-and-change-tracking.md](permission-modes-and-change-tracking.md) and
[../concepts/hook-bridge.md](../concepts/hook-bridge.md) for the machinery this sits on.

## Thesis: review is post-hoc, so reject is the only action that touches disk

In `default` mode, `openDiff` is a **gate** — it blocks *before* the write, and Keep/Reject decides
whether the edit ever happens. In an auto-apply mode there is no gate: the edit is already on disk by
the time you look at it (that is the entire point of the mode). So review is **post-hoc**, and that
inverts what the two actions mean:

- **Revert = the only action that mutates the file** — it undoes a change that already landed. It removes
  the change from disk, so it vanishes entirely (nothing left to fade); it stays recoverable through the
  undo history (`Ctrl+Shift+Backspace` / Redo), not an inline affordance.
- **Keep = advance the review baseline + fade the hunk.** No disk write — the change is already there — but
  not a no-op: it advances **Core's** review baseline over the kept change so the hunk leaves the *bright
  pending* band. It does **not** vanish: it stays visible as a **faded "accepted" band** — proof it was
  kept, still recoverable — with an inline **↶ undo** beside it, until **Keep-all** or the **next prompt**
  commits it. See [The faded "accepted" band](#the-faded-accepted-band-keep-fades-a-hunk-it-doesnt-hide-it).
- **Do nothing = keep, but unreviewed.** Whatever you don't touch stays on disk *and stays in the
  review set* (see [Accumulate](#accumulate-the-baseline-is-last-reviewed-not-turn-start)).

The asymmetry (revert mutates the file; keep only advances the baseline) is the load-bearing idea.

## Accumulate: the baseline is "last reviewed", not "turn start"

The review set is **everything Claude changed that you haven't acknowledged yet** — not just the most
recent turn. A change leaves the set **only** by an explicit Keep / Keep-all or a Revert; doing nothing
keeps it *unreviewed*, and it persists across as many turns as you like. Fire three quick prompts in a
bypass workflow, then walk the accumulated set once.

Mechanically this means the diff baseline is each file's **last-reviewed content**, which advances only
on Keep/Revert — *not* a per-turn reset. This is simpler than a per-turn model (one baseline that
advances on review, instead of a reset every prompt) and it is the only model that doesn't force a
review gate between turns. The cost is review *debt* that can pile up, so it must never be invisible —
the toolbar's presence and its file counter are the debt indicator, and **Keep-all** clears it in one
key.

> This reverses the earlier "not acting auto-confirms at the next turn" framing, which only makes sense
> in a turn-scoped model. If a turn-scoped option is ever wanted, "submitting a new prompt keeps the
> current set" becomes a single setting on top of this — but accumulate is the default.

## Scope

- **In scope:** the `acceptEdits` and `bypassPermissions` modes, where edits auto-apply (collectively
  "auto-apply modes"). The post-turn surface only arms in these.
- **Out of scope:** `default` mode. There, `openDiff` is the blocking review surface and this flow does
  not apply (the host suppresses the post-turn surface exactly as it suppresses `turn-diff` in
  `default`).

## The one surface: the hovering inline toolbar

The inline-diff renderer (`src/web/src/editor/inline-diff.ts`) already paints a turn's applied changes
in the live editor (added lines, removed ghosts, char-level highlights) with a small floating toolbar
pinned bottom-center. In an auto-apply turn that toolbar **is** the review UI — a 2D navigator wrapped
around a **scope picker**:

| Keys | Action |
|---|---|
| `↑` / `↓` (`ctrl+$mod+Up` / `ctrl+$mod+Down`) | previous / next **change** (hunk) within the file |
| `←` / `→` (`ctrl+$mod+Left` / `ctrl+$mod+Right`) | previous / next **file** in the review set |
| **Keep** (`$mod+Enter`) | keep at the toolbar's **scope** (this change / file / all), advance |
| **Revert** (`$mod+Backspace`) | revert at the toolbar's **scope** (this change / file / all), advance |
| Undo keep (`$mod+Shift+Enter`) | undo the most recent keep — bring its change back into the pending set |
| Undo revert (`$mod+Shift+Backspace`) | undo the most recent revert — restore its change on disk |

The vertical axis walks hunks; the horizontal axis walks files. A **stacked label** names where you are
— the filename over a `file i/N · change j/M` subtitle, with change-position dots — and a `Scope: <X> ▾`
picker (This change / This file / All files, each annotated with its reach) chooses what the singular
**Keep** / **Revert** buttons act on. The picker is **sticky** across files (reset only on a turn-reset).

The plain **Keep** / **Revert** keys act at the toolbar's **sticky scope** — the same scope the buttons
follow — so a keypress always matches what the picker shows. The modifier picks *direction*, not scope:
`$mod+Shift+Enter` / `$mod+Shift+Backspace` **undo** the last keep / revert (see [Undo/redo](#undoredo)).
Every keep and revert is undoable, so a misfire is one keystroke from recovery. Keep-all / Revert-all (the
whole set) live in the picker's **All** scope. **Revert file and Revert all still confirm** before
discarding a batch, though both are now undoable.

All the diff/review chords carry a `!terminalFocused` **per-binding** guard, so a routine `$mod+Backspace`
(delete-word) in the embedded Claude never reaches into the editor to revert a hunk — yet the commands
stay in the palette regardless of focus (a per-binding `when` doesn't gate palette visibility).

Every button advertises its live shortcut on hover via the command catalog (`formatKey`), per the
keyboard-first rule — bindings are never hardcoded, and the Keep/Revert tooltips name the current scope.

### Parked navigator — surface live, never hijack the editor

The review **surfaces itself the moment a change lands**, but it never moves your editor. As soon as the
review set is non-empty, the toolbar appears over the editor pane in a **parked** state — the same
bottom-center bar as a live review, sitting at "change 0" — regardless of what file you're looking at. It
reads `N files · press ↓ to start`; only the nav affordances are lit. Any nav (`↑`/`↓`/`←`/`→`) or `Keep`
**steps in** — opening the first changed file at its first hunk, where the bar expands into the live
per-hunk toolbar. Keep/Revert are inert while parked; Undo/Redo still reflect the session history.

This replaces the old "jump the editor to the first changed file when the turn goes idle" behavior: the
editor is **never** auto-moved (mid-turn or at turn-end). Stepping in is always user-driven. The host just
pushes the review set live (on every edit, via `Changes.Changed`); the page decides to park or expand it
purely from whether a changed file is in view — so there's no host-side `open` flag or auto-arm bookkeeping,
and it's race-free across a session switch by construction (parking never touches the editor). The
`weavie.review.open` command (palette) still jumps to the first change on demand.

## What already exists (reused, not rebuilt)

- **Auto-apply detection** — Claude owns its edit mode (Shift+Tab); Weavie observes it off the hook
  stream (`ObservedPermissionMode`). `AutoAppliesEdits` drives the openDiff auto-keep only; the turn push is
  unconditional (the change tracker records edits in every mode, so the navigator is the review surface in all
  modes, default included).
- **Per-turn change tracking** — `SessionChangeTracker` keeps the review baseline per file and exposes
  `TurnChanges()` / `GetTurn(path)`. (Accumulate changes *when* that baseline advances — see below.)
- **Inline diff renderer** — decorations + ghost view-zones + the floating toolbar + **hunk navigation**
  (`goToChange`, every hunk anchored in `currentChangeLines`), with an `"applied"` mode for auto-applied
  turn changes.
- **Open-file-and-jump** — `EditorController.openFile(path, line)` plus the inline hunk nav already do
  "open this file and land on a change."
- **Host↔web turn messages** — `turn-changes` (the review file list, gated to auto-apply modes),
  `turn-diff` (per file), `turn-reset`, and `get-turn-diff`, built centrally in `ChangeMessages.cs`.

## The model

### Three layers, three owners

| Layer | Owner | Lifetime |
|---|---|---|
| Disk truth (file contents, review baseline + accepted anchor, the keep/revert/un-keep baseline advance, the revert write) | **Core** (`SessionChangeTracker` + host) | session |
| Hunk geometry (which lines are a change) + the faded band's model-line placement | **Web** (VSCode `linesDiffComputers`, `reviewToModelLine`) | recomputed per render |

Both kept and reviewed-state are **Core-owned** now: a Keep advances `_reviewBaseline` (the same field a
revert and keep-all advance), so there is no web-side "reviewed marks" layer to persist or reconcile, and
nothing about review progress is lost on a session switch. The web holds only the transient hunk geometry.

### Hunk independence

The user reviews hunk-by-hunk, and **keeping or reverting one hunk must not disturb the others' content**.
There is a guarantee underneath: **two separate hunks are always separated by ≥1 equal line** — that is
what makes them two hunks. Keeping a hunk advances the baseline over just that hunk's lines (making that
region equal to current); reverting replaces its current lines with its baseline lines (making that region
equal to the baseline). Either way the operation only ever *adds* equality to one region — it can never
merge two neighbours or alter another hunk's text, only shift line numbers. Keep additionally leaves the
**live model untouched** (no disk write), so every other hunk's current-side coordinates stay valid and the
walk can reveal the next hunk optimistically before the host re-emits the trimmed diff.

### How revert (per-hunk) works

This is the one genuinely new capability. The web has the authoritative diff: VSCode's
`linesDiffComputers.getDefault().computeDiff(baseline, liveModel)` yields `LineRangeMapping`s, each with
an `originalRange` (into the baseline) and a `modifiedRange` (into the current file). To revert hunk *h*,
its `modifiedRange` lines are replaced by its `originalRange` lines from the baseline.

To honor the hook-bridge security rule — *destructive UI actions operate on real file state, never on
content supplied over a message* — the splice happens in **Core**, sourcing the replacement text from
Core's own baseline. The web sends only **coordinates and a guard**:

1. **Web → host** `reject-hunk { path, baselineStart, baselineEndExclusive, currentStart,
   currentEndExclusive, guardText }`. Ranges are 1-based, end-exclusive (matching VSCode line ranges).
   `guardText` is the exact current text of `[currentStart, currentEndExclusive)` as the web sees it —
   the optimistic-concurrency check, not the content to write.
2. **Host (Core):** read the file's current content. Confirm `[currentStart, currentEndExclusive)`
   equals `guardText`; if it differs (a parallel agent or a later Claude edit moved the file), **abort
   and toast** "file changed — re-open to review," and re-emit a fresh `turn-diff`. No write.
3. On match, build the new content: current lines with `[currentStart, currentEndExclusive)` replaced by
   the baseline lines `[baselineStart, baselineEndExclusive)`. Write through the existing save path,
   update `_current[path]`, and re-emit `turn-diff` so the inline diff drops that hunk.
4. The web re-renders; the reverted hunk is gone, the cursor lands on the next hunk.

**Flush before revert.** Saves are debounced, so before issuing `reject-hunk` the web flushes the
working copy (`flushSave`) so `guardText` reflects what Core will read. After Core writes, `fs-change`
reloads the model without marking it dirty.

**Reverting a created file deletes it.** A file created since the baseline has an empty baseline, so its
whole content is one added hunk. Reverting it returns the file to its baseline state — which is
*non-existent*, not empty — so the revert **deletes the file** rather than leaving a 0-byte file.
Deletion keys off **existence at baseline, not emptiness** (a `_createdSinceBaseline` set the tracker
records on first capture). Per-hunk, per-file (`revert-file`), and whole-set (`undo-turn`) reverts all
route through `SessionChangeTracker.RevertFile`/`RevertHunk`, so the delete-vs-truncate rule is identical
across the three.

### How keep works

Keep advances **Core's** review baseline — no disk write, but the kept change leaves the pending diff for
good (it survives session switches; the earlier web-only "reviewed marks" model lost kept state on a
switch). It mirrors revert's coordinate protocol exactly, only it splices the *current* lines into the
baseline instead of the *baseline* lines into the file:

1. **Web → host** `keep-hunk { path, baselineStart, baselineEndExclusive, currentStart,
   currentEndExclusive, guardText }` — the same shape and `guardText` optimistic-concurrency check as
   `reject-hunk`. The web flushes the working copy first so `guardText` matches what Core reads.
2. **Host (Core):** confirm `[currentStart, currentEndExclusive)` equals `guardText`; on mismatch, toast
   "file changed — re-open to review" and re-emit a fresh `turn-diff` without advancing. On match, splice
   the current hunk's lines into `_reviewBaseline` over `[baselineStart, baselineEndExclusive)` (no file
   write), then re-emit `turn-diff` — now review-baseline-equals-current over that region, so the hunk
   leaves the *bright* band and reappears *faded* (`accepted anchor → review baseline`, see below).
3. The web reveals the next bright hunk; when the file's last bright hunk is kept, review baseline equals
   current (no pending hunks) but the file **stays** in `turn-changes` carrying its faded band — it only
   drops out when the **accepted anchor** catches up, at Keep-all or the next turn start.

**Keep file** (`keep-file { path }`) advances the whole file's review baseline to current in one step, so
the entire file goes faded; **Keep-all** (`accept-turn`) advances *both* the review baseline and the
accepted anchor for every file — the commit point that clears every marker (bright and faded). The
accumulate baseline is exactly the union of kept content, and because it lives in Core it is identical on
every session the file is viewed from.

### The faded "accepted" band: Keep fades a hunk, it doesn't hide it

A kept hunk that simply *disappeared* gave no proof it had been accepted and no in-place way back. So Core
keeps a **third** per-file content alongside the review baseline: the **accepted anchor**, the file's
content at the last Keep-all, advanced **only** by Keep-all. With it, each file splits into two bands:

- **Pending (bright green)** = `review baseline → current` — Claude's still-unreviewed changes.
- **Accepted (faded green)** = `accepted anchor → review baseline` — hunks you've kept but not yet
  committed.

A Keep advances the review baseline over the hunk, so the hunk slides from the bright band into the faded
band; **Keep-all** advances the accepted anchor to current, collapsing the faded band to nothing (the
commit point). The faded band is **turn-scoped**, unlike the pending set: submitting a new prompt
(`UserPromptSubmit`, the turn-start boundary) advances every accepted anchor to its review baseline —
implicitly committing whatever was kept, so accepted changes disappear from the diff view when a new turn
starts. Only the *unreviewed* debt accumulates across turns; keep-proof does not.

**Inline ↶ undo (un-keep).** Each faded hunk carries an inline **↶ undo** beside it (and `Ctrl+Shift+Enter`
un-keeps the most-recent keep via the [history](#undoredo)). It posts `unkeep-hunk { path, acceptedStart,
acceptedEndExclusive, reviewStart, reviewEndExclusive, acceptedGuardText, guardText }` — the inverse of
`keep-hunk`, operating on the `accepted anchor → review baseline` span (both Core-internal, no disk). Core
splices the accepted anchor's lines back into the review baseline over `[reviewStart, reviewEndExclusive)`,
so the hunk returns to the bright band. Both sides carry the text the web rendered as a guard: a mismatch on
`guardText` (a concurrent keep moved the review baseline) or on `acceptedGuardText` (a turn boundary
committed the anchor while the click was in flight) aborts with a re-emit — the splice can only ever restore
exactly the lines the user saw. It deliberately does **not** touch the LIFO undo history, so it composes
with `Ctrl+Shift+Enter` without disturbing the stack (a stale stack entry just declines via its own guard).

**Rendering (web).** The bright band is the existing `diff(review baseline, model)` pass, untouched — so
the per-hunk keep/revert coordinates and the ↑/↓ nav still key on the review baseline. The faded band is a
separate overlay: `diff(accepted anchor, review baseline)` gives each faded hunk's `(acceptedRange,
reviewRange)` directly (the `unkeep-hunk` payload), and its review-baseline position is mapped onto a live
model line via `reviewToModelLine` (the bright diff's line deltas), since a kept hunk sits in a region the
review baseline and model agree on. The faded band never enters `currentHunks`, so it is a pure visual +
inline-undo overlay — the nav and Keep/Revert only ever touch bright pending hunks.

## Undo/redo

Every keep and revert is **undoable**, so review is forgiving — a misfire (or the old "Ctrl+Backspace in
Claude reverted a hunk" footgun) is one keystroke from recovery. The history lives in
`SessionChangeTracker` (per session, so it survives switches like the baselines do) as a stack of
mementos: each keep/revert snapshots the affected paths' full review state — plus the on-disk content for
reverts, which mutate the file — so the action can be **reversed** (undo) or **re-applied** (redo)
uniformly.

- **Undo keep** (`$mod+Shift+Enter`) rolls the review baseline back over the kept hunk, so it returns to
  the pending set. No disk write (keep never wrote).
- **Undo revert** (`$mod+Shift+Backspace`) rewrites the reverted change back to disk (re-creating a file a
  revert deleted). The chords are **type-split** — Shift+Enter only undoes keeps, Shift+Backspace only
  reverts — while the toolbar's single **Undo** button reverses the most recent of either kind.
- **Redo** (toolbar / palette, no key) re-applies the most recently undone action.
- A `!terminalFocused` guard + an availability check let an undo chord **decline** (fall through) when
  there's nothing of that kind to undo.

Undo is **guarded**: an action is reversible only while the paths it touched still match its post-action
snapshot. A newer edit to the same file blocks the out-of-order undo (a toast, not a clobber) — the same
optimistic-concurrency stance as the per-hunk guard.

**Commits clear the history.** Keep-all advances every review baseline to current *and clears the history* —
accepted changes are locked in, so there's nothing to undo past a commit. The **turn boundary** is the other
commit: when a new prompt commits a non-empty faded band, the history clears with it (a stale keep/revert
snapshot could otherwise restore an old anchor and resurrect committed hunks). A boundary with nothing kept
is a no-op and leaves the history alone.

The host bridges this with two messages (`review-undo` carrying an optional `kind`, `review-redo`) and
re-pushes a `review-history { canUndo, canUndoKeep, canUndoRevert, canRedo }` after every review op so the
toolbar's Undo/Redo buttons and the chords' decline stay in sync. `RevertAll` (Revert-all) is a single
undoable step covering the whole set, not a per-file loop.

## Commands & keybindings

Every keybound diff/review command carries a `!terminalFocused` **per-binding** guard so a terminal
keystroke (e.g. `$mod+Backspace` = delete-word in Claude) is never hijacked, and each web handler
additionally **declines** (returns false, falling through to the editor) when there's nothing to act on,
so the editing meaning is unchanged outside a review. A per-binding `when` does **not** gate palette
visibility, so the commands stay runnable from the palette regardless of focus.

| Command id | Default key | Action in review context |
|---|---|---|
| `weavie.diff.nextChange` | `ctrl+$mod+Down` | next hunk |
| `weavie.diff.prevChange` | `ctrl+$mod+Up` | previous hunk |
| `weavie.diff.accept` | `$mod+Enter` | **Keep** at the toolbar's scope, advance |
| `weavie.diff.reject` | `$mod+Backspace` | **Revert** at the toolbar's scope, advance |
| `weavie.review.undoKeep` | `$mod+Shift+Enter` | **Undo keep** — re-pend the most recent kept change |
| `weavie.review.undoRevert` | `$mod+Shift+Backspace` | **Undo revert** — restore the most recent reverted change on disk |
| `weavie.review.redo` | _(palette/toolbar)_ | **Redo** the most recently undone keep/revert |
| `weavie.review.keepFile` | _(palette + scope picker)_ | **Keep file** (= Keep at scope "File") |
| `weavie.review.revertFile` | _(palette + scope picker)_ | **Revert file** (= Revert at scope "File"; confirms) |
| `weavie.review.keepAll` | _(palette-only)_ | **Keep all** — the commit point; clears the marks + undo history |
| `weavie.diff.undo` | _(palette-only)_ | **Revert all** — undo the whole set on disk (confirms; undoable) |
| `weavie.review.nextFile` | `ctrl+$mod+Right` | next file in the review set (land on first change) |
| `weavie.review.prevFile` | `ctrl+$mod+Left` | previous file in the review set |
| `weavie.review.open` | _(palette-only)_ | open the first reviewed file at its first change |

Navigation rides `ctrl+$mod`: plain Ctrl+arrows on Win/Linux, ⌃⌘+arrows on Mac — so ⌘+arrows keep their
macOS line/document meaning even mid-review. On Win/Linux `Ctrl+Left/Right` override Monaco's
word-navigation **only while an applied-mode diff is active** (the handlers decline when there is no
review diff), so normal editing keeps word-nav. The
`$mod+Shift+Enter`/`$mod+Shift+Backspace` chords (formerly Keep file / Revert file) now mean **Undo keep
/ Undo revert**; file-scope keep/revert moved onto the sticky scope picker. New commands follow the
standard path: declare in `CoreCommands.cs` (`RunsIn = Web`), mirror the id in
`src/web/src/commands/types.ts`, register a web handler in `App.tsx`. The sticky **scope** the toolbar
buttons act on is web-only UI state — not a command.

## Message protocol

Built in `ChangeMessages.cs` so both hosts emit identical payloads.

**Host → web**

| type | when | payload |
|---|---|---|
| `turn-changes` | review set updates / turn end, auto-apply modes only | `{ files: [{ path, name, added, removed, line }] }` (a file stays in the set while only faded hunks remain, until Keep-all or the next prompt commits them) |
| `turn-diff` | per file, on change and after a keep/revert/un-keep | `{ path, name, acceptedBaseline, baseline, current }` — the (accepted anchor, review baseline, current) triple |
| `turn-reset` | the whole set was committed (`accept-turn`) | `{}` |
| `review-history` | after every review op + switch-in | `{ canUndo, canUndoKeep, canUndoRevert, canRedo }` |

**Web → host**

| type | when | payload |
|---|---|---|
| `get-turn-diff` | the walk opens a file | `{ path }` → host replies `turn-diff` |
| `keep-hunk` | user keeps a hunk | `{ path, baselineStart, baselineEndExclusive, currentStart, currentEndExclusive, guardText }` → host advances `_reviewBaseline` over it (no disk write; the hunk goes faded; reuses `SessionChangeTracker.KeepHunk`) |
| `unkeep-hunk` | user un-keeps a faded hunk (inline ↶ undo) | `{ path, acceptedStart, acceptedEndExclusive, reviewStart, reviewEndExclusive, acceptedGuardText, guardText }` → host splices the accepted anchor's lines back into `_reviewBaseline` (no disk write; the hunk goes bright; `SessionChangeTracker.UnkeepHunk`). Both sides are guarded: a moved review baseline OR a moved anchor (a turn boundary committed it mid-flight) aborts. |
| `reject-hunk` | user reverts a hunk | `{ path, baselineStart, baselineEndExclusive, currentStart, currentEndExclusive, guardText }` |
| `keep-file` | user keeps a whole file | `{ path }` → host advances its baseline to current (reuses `SessionChangeTracker.KeepFile`) |
| `revert-file` | user reverts a whole file | `{ path }` → host restores it to baseline (reuses `SessionChangeTracker.RevertFile`) |
| `accept-turn` | Keep-all (commit; clears history) | `{}` |
| `undo-turn` | Revert-all (one undoable step via `SessionChangeTracker.RevertAll`) | `{}` |
| `review-undo` | undo the last keep / revert (or generic) | `{ kind?: "keep" \| "revert" }` |
| `review-redo` | redo the last undone action | `{}` |

`turn-changes` is gated to auto-apply modes in the host push (mirroring how `turn-diff` is suppressed in
`default`).

## Architecture / placement

```
src/Weavie.Core/
  Changes/
    SessionChangeTracker.cs   // accumulate: the review baseline advances on keep/accept, NOT on every
                              //   BeginTurn; KeepHunk/KeepFile (advance baseline, no disk write) +
                              //   RevertHunk/RevertFile (concurrency guard + delete-on-revert for created files)
    ChangeMessages.cs         // turn-changes / turn-diff (re-emitted after a keep or revert) / turn-reset
  Commands/
    CoreCommands.cs           // weavie.review.{open,nextFile,prevFile}; ToggleChanges removed; reviewActive when
src/Weavie.Win | Mac | Linux/ // handle get-turn-diff, keep-hunk/keep-file, reject-hunk/revert-file; re-emit
                              //   turn-diff post-keep/revert; the session-changes "show changes" feed is removed
src/web/src/
  editor/inline-diff.ts       // applied mode = the 2D navigator: ←/→ file nav + `name (i/N)` label,
                              //   per-hunk Keep/Revert, emphasize current hunk, keep-hunk/reject-hunk messages
  editor/editor-controller.ts // holds the review file list; ←/→ steps it; open-at-first-hunk
  App.tsx                     // turn-changes → controller; auto-arm on idle; review.* handlers; no panels
  commands/types.ts           // mirror new command ids; drop toggleChanges
```

The session-changes "show changes" panel and the post-turn review panel (both floating file lists) are
**deleted** — `ChangesPanel.tsx` is gone, along with `weavie.view.toggleChanges` and the title-bar
"Changes" toggles. The change *tracker* stays (it is the engine behind the inline diffs and the
`path:line` jump links); only the panel surfaces are removed.

## Build sequence

1. **Single surface + file nav (done).** Delete both panels; make the inline `applied` toolbar the only
   review surface with `←`/`→` file navigation, the `name (i/N)` position label, and auto-arm on the
   idle transition. Keep the existing whole-set Accept/Undo as the act mechanism for now. Verify in
   `acceptEdits`: after a multi-file turn the first file opens on its first change and `←`/`→` walk the
   files inline.
2. **Accumulate baseline (Core + hosts).** Stop resetting the review baseline on `BeginTurn`; advance it
   only on keep/accept. Drop the `turn-reset`-on-new-prompt clear. Remove the now-dead session-changes
   feed from all hosts. Verify: changes survive a new prompt and accumulate until kept.
3. **Per-hunk keep (first cut: web-only marks — superseded by step 6).** Repurpose `$mod+Enter` to Keep
   the current hunk in `reviewActive`; per-file auto-advance to the next pending file.
4. **Per-hunk revert (the new Core capability).** `reject-hunk` + `SessionChangeTracker.RevertHunk` with
   the guard, the existence bit + delete-on-revert for created files, the post-revert re-emit, and
   `$mod+Backspace` per-hunk. Tests: reverting a middle hunk restores exactly those lines and leaves the
   others' content intact; guard mismatch aborts without writing; reverting a created file deletes it.
5. **Polish.** Toolbar Keep/Revert relabel with `formatKey` tooltips, current-hunk emphasis, the
   `reviewActive` context wiring, Keep-all header action.
6. **Keep becomes Core-owned (done).** The step-3 web-only "reviewed marks" lost kept state on a session
   switch (the kept hunk reappeared, since Core's review baseline never advanced). Fix: `keep-hunk` /
   `keep-file` advance `_reviewBaseline` in Core exactly as a revert does (no disk write), so a kept hunk
   leaves `TurnChanges()` durably and survives switches. The web's `reviewMarks`/`fileHunks`/hunk-signature
   bookkeeping and the de-emphasised "reviewed" decoration are deleted — a kept hunk simply disappears from
   the diff. Tests: `KeepHunk` middle-hunk advances baseline leaving others pending, guard mismatch is a
   no-op, last hunk drops the file from the review set; `KeepFile` drops the whole file.

## Edge cases

- **User edits a file mid-review.** The inline diff already distinguishes user lines from Claude lines.
  A revert targets a hunk by current line range with a `guardText` check; if the user changed those
  lines, the guard fails and the revert aborts with a re-render rather than clobbering their edit.
- **Parallel agents / later Claude edit moves the file.** Same guard — a mismatch aborts and re-renders,
  never a blind overwrite. Matches the project's "don't fight concurrent edits" stance.
- **Mode switched mid-turn.** The navigator arms only for auto-apply modes; switching to `default`
  mid-turn hides it. Switching `default → acceptEdits` surfaces whatever has accumulated.
- **Reverting a created file deletes it**, and the file may be open in a review tab. The delete goes out
  as an `fs-change` removal; the editor must close that tab cleanly (no "Unable to read file" toast) and
  the walk auto-advances.
- **Binary / very large files.** The tracker is text-based already; the navigator inherits whatever the
  inline diff does today.

## Open questions

- **`Ctrl+Left/Right` vs word-nav (Win/Linux).** Overriding word navigation during an active review is
  the chosen trade for the 2D model; if it grates in practice, `Alt+Left/Right` is the fallback binding.
  On Mac the navigator is `⌃⌘+arrows` — unclaimed by the OS and text editing — so no steal exists there;
  bare Ctrl+arrows would be eaten by Mission Control/Spaces and bare `⌘+arrows` are line/document nav.
