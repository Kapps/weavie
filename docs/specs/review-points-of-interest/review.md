# Review: step through comments and hints

Review of [PR #1217](https://github.com/Kapps/weavie/pull/1217) at `6c647398` against [design.md](design.md): a
`weavie-reviewer` pass, then a duplication pass over the whole diff. Every finding was checked against the code.
The ones marked *observed* were also reproduced in the running app, by running a suite, or in the PR's CI run.
Nothing was fixed in the review stage; the fix stage's outcome is in each Status.

**Recheck at `4d9f4874`.** The fix commit's diff was read and each fix confirmed against the code; the ones with no
test of their own were probed in the running app. All six bug fixes hold, and the PR's CI is green for the first
time. One warning is reopened (W6) and the fixes added one warning (W7) and two nits (N11, N12). Nothing was fixed
in the recheck.

Severity is `bug` (wrong behavior or a red check), `warn` (works, but against the design, a standard, or a likely
user expectation), or `nit`. Status is `open`, `fixed`, or `rejected` with the reason.

## Summary

| Severity | Open | Fixed | Rejected |
| --- | --- | --- | --- |
| bug | 0 | 6 | 0 |
| warn | 0 | 7 | 0 |
| nit | 9 | 3 | 0 |

- **CI is green at `4d9f4874`**: every check passed, including `web checks` and all eight e2e shards. At `6c647398`
  one unit test and three e2e tests failed (B1, B2, B6).
- **The three behavior bugs are fixed** (B3, B4, B5), each with a test.
- **The recheck's two warnings are fixed** (W6, W7) at `e8fda4ca`; `weavie-tester` proved the feature in the real app.
- **The host side** (`Resolved`, the three settings, the toggle handlers, the command catalog) matches the design.

## Findings

### Bugs

| # | Where | Finding | Status |
| --- | --- | --- | --- |
| B1 | `src/web/src/editor/line-end-lane.ts:162`, `src/web/src/editor/line-end-lane.test.ts:44` | **`web checks` is red.** The new `editor.onDidContentSizeChange(schedule)` listener is called on the unit test's fake editor, which does not have it: `TypeError: editor.onDidContentSizeChange is not a function`. *Observed* in CI and locally: `pnpm run test` fails 1 of 1274. | fixed: the lane re-lays out on Monaco's hidden-area change instead of content size, and the test's fake editor has that event |
| B2 | `src/web/e2e/functional/unified-review-wheel.spec.ts:115` | **"wheel scrolling preserves file order and geometry as review editors remount" fails every run.** The review's scroll height changes by 6px while wheeling back up (`34385.36` and `34391.36`, one value expected). *Observed* in CI (shard 8) and 2 of 2 times locally. The cause is not found. Two suspects were tried and ruled out by disabling each and re-running: the fold reveal in `restore` (B3) and the compact header's `padding-block`. Not yet tried: the two new content-size listeners, and the compact header's icon buttons. | fixed: the condensed header was 34px against the full header's 33px (its icon buttons used a different box), so the review's scroll extent moved as the header flipped. Both states now use the same `.ui-button`, borderless when condensed; the wheel spec passes 3 of 3 |
| B3 | `src/web/src/editor/review/review-editor.ts:247` | **Every restore now opens folded context, not just a step.** The fold reveal sits in the shared `restore`, which `refreshContext` (line 338) and the tab's view-state restore (`src/web/src/editor/review/review-surface.ts:200`) also call with a captured location. That location is the viewport's centre line when the cursor is off screen, and it can be a folded line. *Observed* on the `unified-review-context.spec.ts` fixture (bands of 36, 63, 36 lines): clicking only the first band also opens four lines of the middle one (`59, 36` left); opening another file and coming back turns the middle band into 59 with nothing clicked. Found by `weavie-reviewer`, re-run here. | fixed: `ReviewEditor.reveal` opens a fold and `restore` never does; the surface uses `reveal` only for a navigation to a line (`"line"` alignment). Unit test: a saved place is restored without a reveal |
| B4 | `src/web/src/editor/review-items/review-item-commands.ts:31` | **A step within the editor already on screen ends on the item's as-made line, not where the item sits.** `setCurrent` runs the claim effect at once, which puts the cursor on the placed line; the reveal on the next lines then moves it to `target.line`. The design has the reveal first and the claim correcting it, which only holds when the file's editor is not up yet. The deleted hint stepper returned before the reveal when the hint was already placed. *Observed*: with six lines typed above two hints, Next put the cursor on lines 20 and 3 while the hints sat on lines 26 and 9. Left-side threads are numbered against the merge-base, so they hit this with no local edit; with B3 the wrong line's fold opens too. | fixed: a step reveals at `claimedLine`, where the claiming editor placed the item, read from the hint's moving mark (`lineOf`) so it is exact right after an edit; a file tab that claimed the item is not reopened. e2e: a step after three lines typed above a hint lands on its new line |
| B5 | `src/web/src/editor/pr-comments/pr-comment-controller.tsx:267` | **Comment on Line with comments hidden opens a new thread on a line that already has one.** Showing comments is now a command round trip, so the lookup on the next line still sees no thread cards (`threads()` is empty while hidden) and falls through to a new draft. On `main` the switch was a synchronous signal. *Observed*: hide comments, put the cursor on the thread's line, run Comment on Line: a draft card opens and the thread gets no reply box. The hint popover's Comment action takes the same path. The call is also a toggle used as "show": a second call before the option returns hides them again, and it does nothing if the settings write fails. | fixed: the reply target is looked up among every thread's placement, shown or not, and comments are shown with `{ "show": true }`, which the toggles now accept, so a repeat or a race never hides them. e2e: Comment on Line with threads hidden replies to the line's thread |
| B6 | `src/web/e2e/functional/review-items.spec.ts:123`, `src/web/e2e/functional/unified-review-widgets.spec.ts:159` | **Two more e2e tests failed in CI and passed 2 of 2 times locally.** The new filter test did not find the header stepper after the reload and Review Changes (shard 8). "scrolled review rename accepts and cancels while a definition peek is open" timed out on its first click (shard 5). Neither cause is known. A new test that fails on its first CI run is a flake to root-cause, not to re-run. | fixed: the filter test ran Review Changes after a reload that had already restored the review tab, which switches to the single-file view; it now runs Show Session Overview. The rename test lost its line right after PageDown, the action that flips the header, so it most likely shared B2's 1px shift; it passes 3 of 3 locally. Recheck: both passed in the one CI run at `4d9f4874`; the rename test's cause is inferred from B2, not shown |

### Warnings

| # | Where | Finding | Status |
| --- | --- | --- | --- |
| W1 | `src/web/src/editor/review-items/review-sequence.ts:91` | **An item on the line a step starts from is skipped.** From a location, Next takes the first item strictly past it. *Observed*: cursor on a thread's own line with no current item, Next went to another file's thread. In the unified review with no focused file editor the start is the current file's first changed line (`src/web/src/editor/review/review-surface.ts:184`), so a thread on that line is skipped too. The design says "first item after it", so the code follows the design; the first Next in a file is where it shows. | fixed: from a location, an item on that line counts as next (and previous). Unit and e2e tests updated |
| W2 | `src/web/src/editor/review-items/review-items.ts:70`, `src/web/src/editor/review-hints/HintPopover.tsx:77` | **The arrows in a hover popover step from the cursor, not from that hint.** Only a pinned hint is the current item, so a popover opened by hovering shows the bare total and its arrows start from wherever the cursor is. The deleted stepper started from the open hint, pinned or not. Confirmed in the code, not run. | fixed: the position control on a hover popover or card shows that item's place and steps from it (`{ from }`) |
| W3 | `src/web/src/editor/review/unified-review.css:104`, `src/web/src/editor/review-items/review-items.css:17` | **The compact header centres rows of mixed text sizes.** Three rules switch to `align-items: center`, and the actions row holds the stepper's count beside the labelled Review button. `docs/concepts/design-system.md:88` bans this: text beside text aligns on the baseline. Confirmed in the CSS, not checked on screen. | fixed: the compact header is baseline-aligned again; only the text-less divider centres itself |
| W4 | `src/web/e2e/functional/review-items.spec.ts` | **Rows of the design's test table have no test.** The Comments filter row is never clicked (only its count is read). "Escape then Next starts from the cursor" presses Escape and never steps. The key is pressed only in the unified review; the single-file review and plain tab journeys use the palette. A click in the code clearing the outlined thread is not exercised. Nothing steps in a file with shifted lines (B4) or comments while hidden (B5). | fixed: the Comments row is toggled, Escape then Next and a click in the code are both exercised, the key is pressed in a single-file review and in plain tabs, and B4/B5 have journeys |
| W5 | `src/Weavie.Core/Configuration/ReviewItemCommands.cs:24` | **Duplication: the toggle handler is a second copy of the blame toggle's.** Same `Set` call, same "overrides it; unset it to see the change" message, same three-exception catch as `src/Weavie.Core/Configuration/GitBlameCommands.cs:22`. One shared "set this setting from a command" helper would serve both. | fixed: `SettingCommand.Set` is the one setting writer for the blame and review toggles |
| W6 | `docs/specs/review-points-of-interest/design.md:378` | **Checklist step 10 is unchecked, and B1 shows the unit suite was not run before the PR.** Run here: `biome check`, `tsc --noEmit` and `pnpm e2e:perf` pass; `pnpm run test` does not. `dotnet format` and `weavie-tester` were not run in this stage. | fixed: `weavie-tester` ran the real app at `e8fda4ca` and proved five scenarios (stepping and wrap with tooltips, Escape then Next, the filter, the condensed header, the folded hint), with clips under `src/web/e2e/.recordings/poi-*.mp4`; checklist step 10 is checked |
| W7 | `src/web/src/editor/review-items/review-sequence.ts:92` | **New, from the W1 fix: after Escape, Next and Previous both go back to the item just left.** A step from a location now counts an item on that line in both directions, and a step leaves the cursor on its item's line. So reading a hint, pressing Escape to close it, then Next reopens the same hint; moving on takes a second press, and Previous cannot go back in one. A click on the item's own line does the same. *Observed*: on item 2 of 3, Escape then Next gave `2 of 3`, and Escape then Previous gave `2 of 3`. The new e2e test asserts this for Next (`src/web/e2e/functional/review-items.spec.ts:110`). W1 (an item on the starting line was skipped) and this pull against each other: the line a step left the cursor on needs telling apart from a line the user moved to. | fixed: the last step records its item and where it left the cursor (`reviewItems.land`); a step from that same line starts from that item (`resumeFrom`), so after Escape or a click on the item's own line Next and Previous move on. A line the user moved to still counts its own item (W1). e2e: on item 1, Escape then Next goes to item 2, and Escape then Previous goes back to item 1; `src/web/e2e/functional/review-items.spec.ts` |

### Nits

| # | Where | Finding | Status |
| --- | --- | --- | --- |
| N1 | `src/web/src/editor/pr-comments/pr-comment-controller.tsx:312` | The thread controller clears the outlined thread on every Monaco mouse-down, including one on a hint popover (a content widget, so Monaco reports it). The hint controller guards the same case with `onPopover` (`src/web/src/editor/review-hints/review-hint-controller.tsx:184`). | open |
| N2 | `src/web/src/editor/pr-comments/PrThreadCard.tsx:110` | The Resolved badge is only in the collapsed summary row. A step always expands its thread, so with "Resolved and outdated" on, the stepper lands on a resolved thread that no longer says it is resolved. | open |
| N3 | `src/web/src/chrome/ContextMenu.tsx:266` | A checkable row keeps `role="menuitem"` with no `aria-checked`; it should be `menuitemcheckbox`. | open |
| N4 | `src/Weavie.Core/Commands/CoreCommands.ReviewHints.cs:117` | `weavie.reviewHints.close` is still titled "Close Review Hint" and described as closing the popover; it now also clears the outlined thread. | open |
| N5 | `docs/specs/review-points-of-interest/design.md:131` | The design no longer matches the code it describes. `stepItem` takes `reviewPaths`. `reviewItems` has `reviewPaths`, `isCurrentThread` and `clearThread`, and `position` is not a memo. `review-options.ts` and `review-items.css` are missing from Files. The sort has no "then id" tie-break; same-line order is the wire order kept by a stable sort (`src/web/src/editor/review-items/review-sequence.ts:69`). The size estimate is 450 lines added; the product code is 855. | fixed: design.md matches the code (signatures, files, sort, size) |
| N6 | `docs/specs/review-points-of-interest/challenge.md` | 197 tracked lines describing a design that design.md says "no longer describes this work". Docs state the final state. | open |
| N7 | `docs/concepts/design-system.md:69` | The primitives list does not have the new `.ui-stepper`. | open |
| N8 | `src/web/src/editor/review-items/review-items.ts:43`, `src/web/src/editor/review-items/ReviewItemStepper.tsx:87`, `src/web/src/editor/review/unified-review.css:119`, `src/web/src/chrome/context-menu.css:71` | **Duplication, app.** `samePaths` is the fourth copy of array equality (`src/web/src/editor/line-end-lane.ts:118`, `src/web/src/agent/AgentTranscriptSnapshot.ts:152`, `src/web/src/chrome/SessionPanel.tsx:218`). The compact header's icon-button hover and disabled rules repeat `.ui-stepper button`'s (`src/web/src/primitives.css:248`), and the filter button sits in a one-button `.ui-stepper` to borrow that look. `.context-menu-detail`'s hover rule repeats `.context-menu-keys`'s. | open |
| N9 | `src/web/e2e/functional/review-items.spec.ts:37` | **Duplication, e2e.** `popover` is copied from `src/web/e2e/functional/review-hints.spec.ts:33`. `openReview` retypes the open-PR-#101 steps that `openPullRequestFile` has (`src/web/e2e/harness/review.ts:35`). `next` hand-rolls the platform modifier where the harness uses `ControlOrMeta`. | fixed: `hintPopover` and `openPullRequest` are harness helpers, and the chord uses `ControlOrMeta` |
| N10 | `src/web/e2e/functional/review-items.spec.ts:166`, `src/web/src/editor/review-hints/review-hints-store.ts:33` | A test that branches on what the page shows (`if count === 0`, run Review Changes), so a run does not say which path it took. And `const visible = reviewHintsVisible`, an alias with one use. | open: the test no longer branches (it was rewritten for B6); the alias is left |
| N11 | `src/web/src/editor/review/unified-review.css:115` | New, from the B2 fix: in the condensed header the hover rule sets every action's colour to `--fg`, so Keep all loses its green and Revert pending its red while hovered. *Observed*: hovered Keep all computes to the text colour. | open |
| N12 | `src/web/src/editor/review-items/ReviewItemStepper.tsx:30` | The W2 fix has no test: nothing clicks a position control's arrows, in the header, on a card, or in a popover, and nothing passes `from`. Probed here instead: a hovered hint's popover read `3 of 3` while the header read `2 of 3`, and its Next arrow stepped from that hint; the header's arrow stepped by click. | fixed: a test clicks a hovered hint's Next arrow (it steps from that hint) and the header's (it steps from the current item) |

## Checked and dropped

These were raised during the review and did not hold up.

- **Deleted commands left behind.** No reference to `nextComment`, `prevComment`, `reviewHints.next` / `previous`,
  `hint-order`, `navigate`, `showPrComments`, the removed setters, or `.unified-review-file-step` remains in `src`,
  `tests`, or `docs`.
- **Mock-host specs missing the three new options.** `mockEditorOptions` spreads `DEFAULT_EDITOR_OPTIONS`
  (`src/web/e2e/mock-host.ts:963`), so they are present.
- **A click inside a thread card clearing the thread.** The card layer is a sibling of `.monaco-editor`
  (`src/web/src/editor/zone-cards.tsx:138`), so Monaco does not report it, as the design says.
- **Escape not reaching a thread.** The Escape binding is gated on `editorFocused` only, so it runs with no hint
  open.
- **`ReviewThread.GetHashCode` ignoring `Resolved`.** Equal threads still hash equal; the contract holds.
- **The claim effect looping on its own writes.** Its second run sees no unclaimed thread and returns.
- **`review-options.ts` duplicating an option adapter.** Reading an editor option into a signal per feature is the
  existing pattern (`overview-collapse.ts`, `fullscreen-column.ts`, `MediaPane.tsx`).
- **The content-size listeners breaking the render budgets.** `pnpm e2e:perf` passes, 10 of 10. They are still
  untested suspects for B2.

## Checks

| Check | Result |
| --- | --- |
| PR CI at `4d9f4874` | green: `web checks`, `.NET tests`, e2e shards 1 to 8, native builds on three platforms, CodeQL |
| PR CI at `6c647398` | `web checks` failed; e2e shards 5 and 8 failed (B1, B2, B6) |
| `main` CI at the merge base `87fec18c` | green |
| Recheck probe, run locally on the `4d9f4874` build | W2's arrows step from their item; the condensed header is 33px, the full header's height; W7 and N11 observed |

The fix stage reported `pnpm run test` (1275 passed), `biome check`, `tsc --noEmit` and `pnpm e2e:perf` (10 passed)
locally; the recheck did not rerun them, since CI ran the full suites on the same commit.

The recheck confirmed these by reading the fix commit, with no run of its own beyond CI: B3 (`reveal` opens a fold,
`restore` does not, and only `surface.reveal` uses it; a section registers after its folds are computed, so a reveal
that waits for a section to mount still sees them), B4 (the claimed line is read after `setCurrent`, and a claiming
file tab is not reopened), B5 (the reply target is looked up among all placements), W3 (no `align-items: center`
left on a text row), W5 (one `SettingCommand.Set`, used by both toggles).
