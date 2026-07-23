# Diff Against

Status: implemented (comment authoring deferred — see [#218](https://github.com/Kapps/weavie/issues/218))
Last updated: 2026-07-21

Review the working tree against **any ref** — a branch, tag, commit, or expression like `HEAD~2` — through
the **same accept/reject engine a turn uses** ([turn-review.md](turn-review.md)). No forge, no new session,
no checkout: the review arms on the **current** session, and the diff is pure local git. It is **not**
read-only — you **Keep** a hunk to drop it from the pending set and **Revert** one to back it out on disk,
and later Claude edits **accumulate into the same set**. A pull request is the same surface with a forge
attached (comments + a committed head instead of the working tree); see [open-pr.md](open-pr.md).

## Commands

| id | title | default key | behavior |
|----|-------|-------------|----------|
| `weavie.diff.against` | Diff Against… | `$mod+Shift+d` | Prompt for a ref (typeahead over local **and** remote-tracking branches — `main`, `origin/main`, incl. the checked-out branch; any commit-ish accepted). A `ref` arg — e.g. Claude via `runCommand` — skips the prompt. |
| `weavie.diff.againstParent` | Diff Against Parent | — | The fixed ref `HEAD^`: the last commit's changes plus anything uncommitted. |
| `weavie.diff.againstHead` | Diff Against HEAD | — | The fixed ref `HEAD`: the uncommitted changes. |

All three are `RunsIn = Web` (the prompt is a web modal, mirroring `weavie.pr.open`) and funnel into one
host message: `diff-against { ref }`.

## Thesis: the review baseline is just a seed

The turn-review engine (`SessionChangeTracker`) diffs each file's current disk content against a per-file
**review baseline**, with an **accepted anchor** behind it for the faded band. The only thing that makes it
"a turn" is where the baseline is **seeded**: normally from each file's disk content the moment Claude first
edits it (`CaptureBaseline`, off the hook stream). **Seed the baseline from a git ref instead** — the ref's
merge-base with HEAD — and the entire Keep / Revert / keep-all / undo / faded-band machinery works unchanged.
Because later Claude edits merely extend `_current` through the same hook path, **new-turn changes accumulate
into the same set for free**. The producer builds every `ReviewSeed` off-thread, then `ArmReview` validates the
whole set against disk and replaces the board in one tracker transaction. Per-file baseline seeding stays an
internal tracker operation, so neither a racing `CaptureBaseline` nor a stale async arm can expose a partial set.

## Semantics

- **Base = `merge-base(ref, HEAD)`** — so diffing against a branch shows only *this side's* changes since
  the fork point (exactly what a PR shows), and a ref that's an ancestor (HEAD, HEAD^, an old commit) diffs
  from itself. A ref *ahead* of HEAD therefore shows nothing of the other side — never a reversed diff.
- **Current = the working tree** — the changed-file list is `git diff --numstat <base>` **plus
  untracked-but-not-ignored files** as all-added entries (`IGitService.DiffWorktreeAsync`); a brand-new file
  is an uncommitted change, so it is never silently absent. Each file at the base (`ReadFileAtRefAsync`, which
  preserves existence separately from empty content) is seeded as its review baseline, its disk content as
  `current`.
- **Keep / Revert act, exactly as a turn** — **Keep** advances the review baseline over the hunk (no disk
  write; it slides to the faded band with an inline `↶ undo`); **Revert** writes the ref content back over
  the hunk **on disk** (an uncommitted backout you then commit). Reverting the last hunk of a file added
  since the ref **deletes** it. Keep-all / undo-all / the undo history are all the turn-review ones.
- **Arming commits any pending turn review.** Because the review shares the session's one tracker, arming a
  ref review snaps the board clean first (`AcceptTurn`) so a file the session already changed that now equals
  the ref leaves the walk, then seeds the ref diff. This is unreachable-in-practice for the real callers (a
  PR session is review-only; a "vs HEAD" arm with pending edits shows those edits *as* the diff), but it is
  the deliberate consequence of one shared engine.

## The shared review-diff surface

The session's `SessionChangeTracker` owns the one durable `ReviewIdentity` (merge-base, label, and optional
PR/forge identity). `HostCore.DiffReviews.cs` owns only a token-keyed, re-fetchable PR-comment cache. Both
producers atomically **arm and seed the tracker** and then ride the shared turn-review messages; keep/revert
need **no** review-specific host code (the existing `keep-hunk` / `reject-hunk` / … act on
`session.Changes`). See [persistent-reviews.md](persistent-reviews.md).

```mermaid
flowchart LR
  PR["open-pr flow<br/>(PrNumber > 0, forge comments)"] --> A[atomic arm · monotonic token]
  PR --> C
  DA["diff-against &lt;ref&gt;<br/>(PrNumber 0, local only)"] --> A
  A -->|ReviewIdentity + ReviewSeed[]| T[SessionChangeTracker]
  T -->|turn-changes · label + files| Web[inline-diff · applied mode]
  T -->|get-turn-diff → turn-diff · accepted/baseline/current| Web
  C["re-fetchable comment cache<br/>review-lifetime fenced"] -->|review-comments · PR only| Web
  Web -->|keep-hunk / reject-hunk / … | T
```

- The `turn-changes` push carries a `label` ("PR #12" / "vs main"), read from the active review, shown in the
  navigator subtitle + parked bar, so the surface always names what it's diffing against. It's threaded at the
  one push site (`PushTurnChangesToWeb`), so a plain turn just carries an empty label.
- **Comments are a PR-only overlay.** A PR pushes `review-comments { number, path, comments }` per file, so
  the inline diff anchors threads and shows a **Comment** button beside Keep/Revert. A local ref pushes none
  (no forge behind it → no comment affordance). Authoring a *new* comment inline is deferred (see below).
- **Arming opens + renders the first changed file** (a review surfaces its code; post-turn review parks). A
  switch-in re-surfaces the active review from the **persisted tracker** (`SurfaceActiveReviewOnSwitch`) — no
  per-switch git diff, so the stale-diff race the old fire-and-forget `pr-changes` had is gone.
- **Keep-all drops a local ref review** (its label would otherwise cling to the next plain turn); a PR review
  identity persists while comments are re-fetched after hydration.

## Failure surfaces (all user-facing toasts)

- Unknown ref → `'x' isn't a branch, tag, or commit here.` (`ResolveCommitAsync` also rejects option-shaped
  input at the trust boundary — a web-supplied ref can never be read as a git flag.)
- No common history → `'x' shares no history with HEAD — there's no base to diff from.`
- Nothing differs → `No changes against 'x'.` — and any prior review is **retracted** (the tracker's board is
  committed and an empty review set pushed) instead of arming an unwalkable navigator.

## Deferred

**New-comment authoring** ([#218](https://github.com/Kapps/weavie/issues/218)) — the floating composer is a
Monaco view-zone inside the vscode workbench (shadow DOM), which intercepts its keydowns before the composer's
own submit handler runs. Rendering threads and **replying** work; authoring a brand-new comment via the
composer is deferred until it moves to an app-level overlay outside the view-zone.

## Testing

- `DiffAgainstTests` (Weavie.Hosting.Tests): the flow end-to-end over a real temp repo — HEAD/parent diffs,
  the `turn-diff` baseline/current pair, **a `revert-file` restoring the ref content on disk** (the
  accept/reject unification), unknown ref, empty-diff retraction, merge-base semantics.
- `SessionChangeTrackerTests` (Weavie.Core.Tests): an atomic ref seed reviews as an applied triple; Keep
  advances the baseline with no disk write; Revert writes the ref over the current lines (a created-vs-ref
  file's revert deletes it); both `CaptureBaseline`↔`SeedRefBaseline` race orderings accumulate.
- `diff-against.spec.ts` / `open-pr.spec.ts` (Playwright): the user journeys — Diff Against HEAD with a Reject
  that backs the edit out, the ref prompt + multi-file walk, the no-changes toast, and a PR's Keep/Revert +
  Comment coexisting on the one toolbar (render + reply; authoring is `test.fixme`, #218).
