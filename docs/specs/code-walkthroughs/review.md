# Code walkthroughs: review

Review of [PR #1302](https://github.com/Kapps/weavie/pull/1302) against [design.md](design.md).

- **Reviewed:** `66e17be8` on `research/code-walkthrough`, diffed from its merge-base with `main`, `437e3927`
  (84 files, +4653/−143).
- **How:** one pass by the `weavie-reviewer` agent over the design and the diff, duplication included. Each finding
  below was then checked against the code; three were dropped (see Rejected).
- **Reproduced by the reviewer** with a temporary Playwright probe on the headless project: findings 1 and 2. The
  rest are confirmed by reading the code.
- **Result:** the host side (rules, capture, storage, tools, path containment) holds. The reading tab has eight
  bugs, most in code no test reaches.
- **Rechecked:** `1c165c9d`, the fixes in `ec955b85..1c165c9d` (see Recheck). 27 of the 30 findings are fixed, one
  is reopened, two are fixed in part, and the fixes added two bugs and six nits. All of those were fixed in
  `a85e255c` (see Second fixes).
- **Rechecked again:** `be2e000f`, the fixes in `d63a73d4..be2e000f` (see Second recheck). All ten hold. Open now:
  one small bug the fixes added (39) and one nit (40). Both fixed (see Third fixes).
- **Rechecked a third time:** `572a0bfe`, the fix in `b38a1172..572a0bfe` (see Third recheck). Both hold. Open now:
  one nit in the design's wording (41).

Severity: a **bug** is wrong behaviour a user or another feature would hit; a **warn** is a standards or
maintainability problem; a **nit** is anything smaller.

| # | Severity | Status | Finding | Where |
| --- | --- | --- | --- | --- |
| 1 | bug | fixed | A note, fold row or question under a block's last line is not drawn | `src/web/src/agent/walkthrough/walkthrough-code.tsx:225` |
| 2 | bug | fixed | A stop row on the pane card always opens the tab at stop 1 | `src/web/src/agent/walkthrough/WalkthroughTab.tsx:275` |
| 3 | bug | fixed | Two blocks on one deleted file collide, and any failure to open a file reads "Deleted" | `src/web/src/agent/walkthrough/walkthrough-code.tsx:70` |
| 4 | bug | fixed | A question asked from a walkthrough a `/btw` published is refused as "no longer shown" | `src/Weavie.AgentClientProtocol/AcpAgentSession.SideConversations.cs:28` |
| 5 | bug | fixed | A dismissed question that then needs a permission can't be answered | `src/web/src/agent/walkthrough/WalkthroughCard.tsx:33` |
| 6 | bug | fixed | A whole-line selection puts its question one line low | `src/web/src/agent/walkthrough/walkthrough-code.tsx:255` |
| 7 | bug | fixed | After an update re-records a block, older questions sit under the wrong line | `src/web/src/agent/walkthrough/WalkthroughTab.tsx:119` |
| 8 | bug | fixed | `/btw` cards did not get Stop; the Stop added is invisible | `src/web/src/agent/AgentAsideEntry.tsx:57` |
| 9 | warn | fixed | Scrolling past a stop, opening a fold or revealing an answer repaints every mounted block | `src/web/src/agent/walkthrough/WalkthroughTab.tsx:214` |
| 10 | warn | fixed | Code selection is a second copy of the editor's selection actions and works on every selection event | `src/web/src/agent/walkthrough/walkthrough-code.tsx:248` |
| 11 | warn | fixed | `WalkthroughQuestion` re-wires `AsideEntry` | `src/web/src/agent/walkthrough/WalkthroughQuestion.tsx:30` |
| 12 | warn | fixed | `ReviewHintStore` hashes and splits text `WorktreeTextFile` already hashed and split | `src/Weavie.Core/Review/Hints/ReviewHintStore.cs:86` |
| 13 | warn | fixed | Clicking a question's head dismisses it for good, under a "Collapse" tooltip | `src/web/src/agent/walkthrough/WalkthroughQuestion.tsx:40` |
| 14 | warn | fixed | Stop is a bare bus message, not a command | `src/web/src/agent/walkthrough/walkthrough-ask.ts:34` |
| 15 | warn | fixed | The host checks neither the anchor's place nor its quote | `src/Weavie.Hosting/HostCore.Sessions.cs:81` |
| 16 | warn | fixed | The fold row and strip buttons are styled from scratch, with a font set | `src/web/src/agent/walkthrough/walkthrough.css:162` |
| 17 | warn | fixed | `WalkthroughTab.tsx` is 417 lines doing six jobs | `src/web/src/agent/walkthrough/WalkthroughTab.tsx:83` |
| 18 | warn | fixed | `McpServer` holds a nullable `WalkthroughCapture` | `src/Weavie.Core/Mcp/McpServer.cs:43` |
| 19 | warn | fixed | Tests the design lists are absent | `src/web/e2e/functional/walkthroughs.spec.ts:83` |
| 20 | nit | fixed | A block shows one notice; "Recorded after edits" hides the others for good | `src/web/src/agent/walkthrough/walkthrough-code.tsx:177` |
| 21 | nit | fixed | A failed source request shows the raw error and is cached for the tab's life | `src/web/src/agent/walkthrough/WalkthroughStop.tsx:213` |
| 22 | nit | fixed | An update can blank `title`, `problem` or `gist` with an empty string | `src/Weavie.Core/Agents/AgentWalkthroughRules.cs:56` |
| 23 | nit | fixed | An update accepts `title`, which the design does not list; the pane card keeps the old one | `src/web/src/agent/walkthrough/WalkthroughCard.tsx:45` |
| 24 | nit | fixed | The 2 MB limit is checked after the file is read, split and hashed | `src/Weavie.Core/Agents/WalkthroughCapture.cs:33` |
| 25 | nit | fixed | Three small design items are not built | `src/web/src/agent/walkthrough/WalkthroughTab.tsx:150` |
| 26 | nit | fixed | `Prose` builds a new cache key per read, so its markdown is never cached | `src/web/src/agent/walkthrough/WalkthroughStop.tsx:32` |
| 27 | nit | fixed | Three-line file comment | `src/web/src/agent/walkthrough/walkthrough-code.tsx:1` |
| 28 | nit | fixed | `em` in chrome geometry | `src/web/src/agent/walkthrough/walkthrough.css:107` |
| 29 | nit | fixed | A stop request is keyed by tab path alone, in a module global | `src/web/src/agent/walkthrough/walkthrough-view.ts:19` |
| 30 | nit | fixed | The embedded-editor setup repeats the review's, with a class that has no rule | `src/web/src/agent/walkthrough/walkthrough-code.tsx:78` |
| 31 | bug | fixed | A closed fold under an empty line has no row and can't be opened (from fix 1) | `src/web/src/agent/walkthrough/walkthrough-code.tsx:240` |
| 32 | bug | fixed | The strip shows the wrong current stop after a jump back up the page (from fix 9) | `src/web/src/agent/walkthrough/walkthrough-page.ts:84` |
| 33 | nit | fixed | Any failure to build a block reads "This code is no longer stored" (rest of 3) | `src/web/src/agent/walkthrough/WalkthroughStop.tsx:220` |
| 34 | nit | fixed | A code selection of only blank text offers Send, which the host refuses | `src/web/src/agent/walkthrough/walkthrough-code.tsx:269` |
| 35 | nit | fixed | A code selection over several lines is not lit | `src/web/src/agent/walkthrough/walkthrough-code.tsx:179` |
| 36 | nit | fixed | The waiting row can't reach a question under a block that has not mounted | `src/web/src/agent/walkthrough/WalkthroughTab.tsx:210` |
| 37 | nit | fixed | The prose highlight uses `CSS.highlights` unguarded | `src/web/src/agent/walkthrough/walkthrough-page.ts:152` |
| 38 | nit | fixed | Two doc comments stacked over the new `SelectionActions` type | `src/web/src/agent/selection-actions.tsx:32` |
| 39 | bug | fixed | A code selection that ends on a blank line offers no actions (from fixes 34 and 35) | `src/web/src/agent/walkthrough/walkthrough-code.tsx:273` |
| 40 | nit | fixed | A failed request for a file's `after` copy is an unhandled rejection | `src/web/src/agent/walkthrough/walkthrough-code.tsx:143` |
| 41 | nit | open | The design describes asking from a code selection as first built | `docs/specs/code-walkthroughs/design.md:574` |

## Bugs

### 1. A note, fold row or question under a block's last line is not drawn

Monaco draws a zone only when the line after it is visible (`viewZones.js`: `isVisible =
modelPositionIsVisible(zoneBeforeModelPosition)`). Every line after the block's `to` is hidden
(`walkthrough-code.tsx:136`). `place()` moves a card past a closed fold but does nothing at the block's end, and
the host accepts a note on `to` and a fold ending on `to` (`AgentWalkthroughRules.cs:204`, `:214`).

- A note on a function's closing line is hidden. The probe measured its zone at `top: -1e+06px`.
- A fold that ends on the last line has its row hidden while its lines stay hidden, so it can never be opened.
- A question whose selection ends on the last line goes through the same `place()`, so its answer has no card.

It does not happen when the block ends on the file's last line.

### 2. A stop row on the pane card always opens the tab at stop 1

```ts
const index = stops().findIndex((stop) => stop.id === takeRequestedStop(props.path));
```

`takeRequestedStop` removes the request as it reads it, and `findIndex` calls it once per stop. The first call
returns the id and every later one returns null, so only stop 1 can match. The probe clicked the third stop's row
and the tab opened at `scrollTop 0`. The e2e clicks row 2 and never checks where the tab lands.

### 3. Two blocks on one deleted file collide, and any failure to open a file reads "Deleted"

The snapshot model's URI is `weavie-walkthrough:/<path>?<source>`. Two blocks on the same file and copy build the
same URI; the second `createModel` throws, and Monaco's "Cannot add model because it already exists" becomes that
block's notice.

The `catch {}` around `scope.open` also treats a closed scope, a timeout and a transport error as a deleted file.

`createFileModelScope` already keeps one snapshot per deleted file (`src/web/src/editor/editor-host.ts:609`).

### 4. A question asked from a walkthrough a `/btw` published is refused

`AskAside` looks the block up in `Output.View(_definition.Id, null)`, the primary conversation only, and the anchor
carries no conversation id. A side conversation can show blocks (`AcpSideCollapseTests`), so it can publish a
walkthrough, and the tab path carries its conversation id. Every question asked there fails with "That walkthrough
is no longer shown". The design assumed a walkthrough is always the primary's. Not run.

### 5. A dismissed question that then needs a permission can't be answered

The tab leaves out questions with `asideCollapsed` (`WalkthroughTab.tsx:116`). The pane card's `waiting()` does
not. Dismiss a question while it works; when it asks for a permission the card says "A question is waiting for
you", the tab opens, and no card is there. The side conversation stays working, which also blocks Rewind.
`AsideEntry` handles this with `pendingOnly`.

### 6. A whole-line selection puts its question one line low

The place uses `selection.endLineNumber`. A line selection ends at column 1 of the next line, so the card lands
under the line after the selection. `lastLineOf` (`src/web/src/editor/selection-excerpt.ts:20`) exists for this and
is not used.

### 7. After an update re-records a block, older questions sit under the wrong line

`places()` accepts a code place when its line number is inside the block's current `from..to`. The anchor's line is
numbered in the copy the block had when the question was asked, and nothing records which copy that was. Once
`updateWalkthrough` replaces a stop's code, the card sits under whatever line now has that number, without "Asked
about text that has since changed".

### 8. `/btw` cards did not get Stop

The design decides "Stop appears on `/btw` cards too" and says the same Stop is added to `AsideEntry`.
`AgentAsideEntry.tsx` is unchanged; only `WalkthroughQuestion` calls `stopAside`. The host and wire support exist.

## Warnings

### 9. Scrolling past a stop, opening a fold or revealing an answer repaints every mounted block

`track` runs on every scroll event and reads `getBoundingClientRect` on the scroller and each stop. When the
current stop changes it writes the view state. `openFolds` builds a new `Set` from that state on every read
(`WalkthroughTab.tsx:214`, `WalkthroughStop.tsx:197`), so each mounted block's `paint` and `cards` run again: a
full text diff, `setHiddenAreas`, and every diff zone removed and added. The twelve-block render-budget case the
design lists is not in the diff.

### 10. Code selection is a second copy of the editor's selection actions

`walkthrough-code.tsx:248-260` repeats `src/web/src/editor/editor-selection-actions.ts` without `selectionExcerpt`
or `lastLineOf` (finding 6). It works inside `onDidChangeCursorSelection`, so each step of a drag reads layout,
runs `diffLines` over the whole file and rebuilds the toolbar; the editor's version waits for mouse-up or key-up.
Its `catch {}` falls back to the live line number without saying so.

### 11. `WalkthroughQuestion` re-wires `AsideEntry`

`WalkthroughQuestion.tsx:30-97` assembles the same `AgentCardShell`, `NestedTranscript`, `AgentCardReplyBox` and
paste hint as `AgentAsideEntry.tsx:36-75`. The design shares one body between them, which is also how finding 8
would have been built once.

### 12. `ReviewHintStore` repeats work `WorktreeTextFile` did

`Capture` reads a `WorktreeTextFile`, which computes `Hash` and `Lines`, and returns only its path and text. `Add`
then hashes the text again (`:86`) and splits it again (`:93`). `Hash` (`:135`) is a one-line wrapper over
`WorktreeTextFile.HashOf`; the design moved it.

### 13. Clicking a question's head dismisses it for good

The card passes `onToggle={dismiss}` with `collapsed={false}`, so the head's tooltip reads "Collapse …" and a click
dismisses the question. `ownCardOnFocus` gives the toggle shortcut the same effect. Nothing brings a dismissed
question back.

### 14. Stop is a bare bus message, not a command

`stopAside` publishes straight to the bus. It is not in the palette, can't be bound, shows no shortcut and an agent
can't run it. A new user-facing action takes a command.

### 15. The host checks neither the anchor's place nor its quote

The design says the host checks the anchor's length and characters. `HostCore.Sessions.cs:81` passes `Place` and
`Quote` through as sent, and `AcpAgentSession.Conversations.cs:120` writes them to the started row, which is
journaled and replayed with the pane. The quote is the whole selection.

### 16. The fold row and strip buttons are styled from scratch

`.walkthrough-fold` (`walkthrough.css:162`) and `.walkthrough-strip-stop` (`:257`) are buttons with their own
padding, border, background and `font`, on no `.ui-*` primitive.

### 17. `WalkthroughTab.tsx` is 417 lines doing six jobs

It holds the source cache, question placement, selection, scroll tracking, command ownership and the strip. The
design named `WalkthroughStrip.tsx`, `walkthrough-selection.ts` and `walkthrough-questions.ts` for three of them.

### 18. `McpServer` holds a nullable `WalkthroughCapture`

`_walkthroughs` is `WalkthroughCapture?` and the handler throws when it is null. Its siblings (`_reviewHints`,
`_agents`, `_workflows`) take a `None` instance.

### 19. Tests the design lists are absent

- **Core:** `WalkthroughCaptureTests` (outside the worktree, binary, missing, late).
- **Hosting:** `block-collapsed`, sources across `/clear` and Rewind, restore after a restart, the anchor surviving
  a restart.
- **Web unit:** question grouping and a place that no longer resolves, line mapping, shown range and folds.
- **e2e:** selecting code, Stop, the strip following a scroll, a deleted file, a walkthrough tab over a file under
  review, and the twelve-block render budget.

Findings 1, 2, 3 and 6 are all in that untested code. The verification record already lists the review-bar case as
outstanding.

## Nits

- **20.** `onNotice` picks one notice from a chain. `file.late` comes before "Changed since this walkthrough was
  updated" and "A noted line changed", so a late file never shows either.
- **21.** `WalkthroughStop.tsx:213` shows the error's message; the design's text is "This code is no longer
  stored". The rejected promise stays in the tab's `sources` map, so it is never asked for again.
- **22.** `Optional` returns `""` for an empty string and `Apply` takes any non-null value, so
  `updateWalkthrough` with `"problem": ""` blanks a field publish requires.
- **23.** `Update` reads `request.Title` and the update schema offers it; the design lists `problem`, `gist`,
  `example` and `closing`. The pane card's label is the reference row's `summary`, which an update does not change.
- **24.** `WalkthroughCapture.TryRead` counts bytes after `WorktreeTextFile.TryRead` has read, split and hashed the
  file.
- **25.** Not built: the quoted text staying lit while its card is drawn; selection actions disabled with a tooltip
  when the agent has no `/btw`; the pane card's "A question is waiting for you" row opening the tab at that card
  (it opens at the saved position).
- **26.** `key()` returns a new object on every read, so `renderCached`'s `WeakMap` never hits. Its comment gives a
  reason `renderCached` already covers by comparing content.
- **27.** `walkthrough-code.tsx:1-3` is a three-line comment; the limit is two.
- **28.** `.walkthrough-number { min-width: 1.4em }`; chrome geometry is px.
- **29.** `requested` is a module-level map keyed by tab path, which is the same string in two sessions. Only one
  card tab is mounted at a time, so a wrong tab takes a request only when one is left over from an open that failed.
- **30.** `walkthrough-code.tsx:78-104` repeats the overflow-widgets node and most options of
  `src/web/src/editor/review/review-editor.ts:78-108`. `walkthrough-overflow-widgets` has no CSS rule; the review's
  class positions its node.

## Fixes

Fixed on `research/code-walkthrough` after `ec955b85`. Each change has a test unless noted.

1. Cards hang after the line's last character when the line after it is hidden (`ZoneCard.afterColumn`). e2e: a
   note on the block's last line is in view.
2. The card asks the tab for a target once (`requestedTarget`), and a stop scrolled to stays put while the blocks
   above it mount, until the reader scrolls. e2e: a stop row opens the tab at that stop.
3. The tab asks the host whether the file exists (`files` `stat`), opens the live file when it does, and otherwise
   shares one copy per path and recorded text. Other failures surface as themselves. e2e: two blocks on a deleted
   file.
4. The anchor names the walkthrough's conversation, and `AskAside` looks the block up there. Hosting test.
5. and 13. A question is a BTW card (`AsideEntry`): collapsing it leaves its head, a pending request still shows
   in it, and the pane card's waiting row opens the tab at that card.
6. and 10. Code selections use the editor's own gesture watcher (`watchSelectionGestures`, extracted from
   `editor-selection-actions.ts`), `lastLineOf` and `selectionExcerpt`. e2e: a code question sits under its line.
7. A code place names the copy its line is numbered in (`<stop>/code/<i>/<line>@<copy>`); a place on an older copy
   moves to its stop's end. Unit test.
8. `/btw` cards have Stop, through the new `weavie.agent.stopAside` command (14). e2e.
9. Folds and questions are compared by value, so a stop change repaints nothing; the current stop comes from an
   IntersectionObserver, so scrolling reads no layout. e2e render budget: twelve blocks scrolled top to bottom, at
   most one read per wheel notch, nothing removed.
11. `WalkthroughQuestion` is `AsideEntry` with a label and the quote.
12. `ReviewHintStore` keeps the `WorktreeTextFile` it read.
15. `AgentAsideAnchor.Problem()` checks the place's characters and length and the quote's length. Hosting test.
16. The fold row and strip stops are `.ui-row`s with no font of their own.
17. `WalkthroughTab.tsx` keeps the page; its services are in `walkthrough-page.ts`, `walkthrough-selection.ts`,
    `walkthrough-questions.ts` and `WalkthroughStrip.tsx`.
18. `WalkthroughCapture.None`.
19. Added: `WalkthroughCaptureTests`; hosting tests for `block-collapsed`, copies across Rewind and `/clear`, restore,
    the anchor restored, a question about a `/btw`'s walkthrough; web unit tests for question placement; e2e for
    code selection, Stop, the strip following the reader, a deleted file, the review bar and the render budget.
20. Every notice that applies is shown. 21. A failed copy reads "This code is no longer stored" and is asked again.
22. An update refuses an empty field it gives. 23. `title` is refused on update. 24. Size is checked before reading.
25. The quoted text stays lit (a CSS highlight in prose, a decoration in code) and the waiting row opens at its
    card. Not built: disabling the selection actions when the agent has no `/btw`; the host's refusal already says
    so in a toast, and the page has no list of the agent's commands to ask.
26. One cache key per prose. 27. Two-line comment. 28. px. 29. Requests are keyed by session, then path.
30. `embeddedEditorChrome` and `embedded-editor-overflow-widgets` are shared with the review.

Found while fixing: the pane's wire row never carried the anchor (`AgentPaneWire`), so every question landed in the
pane transcript; the first e2e passed only through `blockId`. Blocks also mounted against the window, so a block
scrolled out of the tab never mounted.

## Recheck

Rechecked at `1c165c9d` (PR #1302, open): the three commits after `ec955b85`, read in full. The design changed only
where the fixes needed it (the anchor's shape, questions drawn as BTW cards, `stat` before opening a file), so the
review was not restarted. Each fix was confirmed against the code and its tests; three points were run with a
temporary Playwright probe on the headless project, removed afterwards. Nothing was fixed here.

**Confirmed fixed:** 1, 2, 4, 5, 6, 7, 9 to 24 and 26 to 30. Notes on the ones with more to say:

- **1.** A card under a line whose next line is hidden hangs after the line's last character. The note on a block's
  last line is drawn (e2e). The same change broke a different case: finding 31.
- **4, 15.** The anchor names its conversation and the host checks the place and the quote before it forks.
- **5, 11, 13.** A question is an `AsideEntry`, so collapsing leaves its head and a pending request still shows.
- **7.** A place carries the first 12 characters of its copy's hash; a question on an older copy moves to its
  stop's end with the notice.
- **9.** A stop change, a fold toggle and a reveal no longer repaint other blocks, and the budget case passes. The
  observer that replaced the layout reads is finding 32.
- **19.** The listed tests exist, except unit tests for line mapping, the shown range and folds. No test places a
  fold row or moves the strip by a jump, which is where findings 31 and 32 are.

### Reopened

**8. The Stop on a BTW card is invisible.** The button's only class is `agent-entry-rewind`, which is `opacity: 0`
unless an `.agent-entry` ancestor is hovered or the button has keyboard focus (`src/web/src/agent/agent.css:373`).
A BTW card has no `.agent-entry` ancestor. The probe read `opacity: 0` on the pane's card, hovered and not, and
the same for a button of that class in a walkthrough question's head. The e2e passes because Playwright treats an
element at `opacity: 0` as visible and clicks it. Before the fix a walkthrough question had a visible Stop icon, so
questions lost it too. The Open button beside it is shown by `.agent-aside-head .agent-entry-open`.

### Fixed in part

- **3.** Two blocks on a deleted file share one copy (e2e), and the tab asks the host whether the file exists. The
  other half moved: see finding 33.
- **25.** The quote stays lit and the waiting row opens at its card. Disabling the selection actions when the agent
  has no `/btw` is still not built, and the reason given does not hold: the page keeps each session's slash commands
  (`src/web/src/agent/agent-controls-store.ts:12`), and `design.md:586` still specifies it.

### New bugs, from the fixes

**31. A closed fold under an empty line has no row and can't be opened.** The fix for finding 1 hangs a card after
column `getLineMaxColumn − 1` and falls back to the whole line when that is below 1, which is every empty line
(`walkthrough-code.tsx:240`). Monaco then looks at the line after it, which the closed fold hides, and draws
nothing. The probe folded lines 3-4 under an empty line 2: the row's zone sat at `top: -1e+06px` and the folded
lines stayed hidden. A blank line before the members an agent folds away is common. Before the fix the row went
after the fold's last line, which drew.

**32. The strip shows the wrong current stop after a jump back up the page.** `trackCurrentStop` remembers, per
stop, whether its heading has passed the top third, and learns it only when the stop crosses that band
(`walkthrough-page.ts:84`). A stop that goes from above the band to below it between two frames never crosses, so
it stays "passed". The probe read down to stop 12, set the scroller to the top, and the strip marked stop 11 with
Previous enabled. Dragging the scrollbar or pressing Home does this. Next and Previous then step from the wrong
stop, and the wrong stop is saved as where the tab reopens.

### New nits, from the fixes

- **33.** `WalkthroughStop.tsx:220` answers every rejection from `createWalkthroughCode` with "This code is no longer
  stored": a failed `stat`, a file that would not open, a closed scope. The fix notes say other failures surface as
  themselves.
- **34.** `watchSelectionGestures` offers the actions for any non-empty selection. A selection of only blank lines or
  indentation gives the quote `""`, `askFrom` sends it as given, and the host refuses with "A walkthrough quote is
  one line of up to 200 characters".
- **35.** The quote is the selection's first line and the mark is looked for on its last line
  (`walkthrough-code.tsx:179`), so only a selection inside one line stays lit.
- **36.** The waiting row reveals `[data-walkthrough-question]`, which exists only once its block's editor has
  mounted. For a question under a block more than a viewport away, the tab opens where it was. Not run.
- **37.** `CSS.highlights` and `Highlight` are used nowhere else in the app. On WebKit before 17.2 they are
  undefined and the effect throws while the tab renders.
- **38.** The doc comment of `createSelectionActions` now sits above the `SelectionActions` type, followed by the
  type's own.

## Second fixes

Fixed after the recheck (`d63a73d4`), each with an e2e case where one applies.

- **8.** Stop carries `agent-entry-stop`, shown in a card head as Open is. e2e asserts its opacity is 1.
- **31.** A card under lines that are hidden goes after the hidden run when a shown line follows it, and otherwise
  hangs after the last character of the nearest line above with text. e2e: a fold under an empty line opens.
- **32.** The current stop's observer band runs from far above the tab to the top-third line, so a stop is in it
  exactly when it has passed, and a jump changes that. e2e: from the last of twelve stops, a jump to the top makes
  stop 1 current and disables Previous.
- **3, 33.** A missing copy reads "This code is no longer stored"; any other failure shows its own message.
- **25.** Send is disabled, with the reason as its tooltip, when the agent's slash commands have no `/btw`.
- **34.** A code selection with no text on its last line offers nothing.
- **35.** A code question quotes the selected text of its last line, where its card and mark sit. e2e: a two-line
  selection is lit on its last line.
- **36.** When the question's card has not mounted, the waiting row opens the tab at its stop.
- **37.** Without the Highlight API (WebKit before 17.2) the quote is simply not lit; nothing throws.
- **38.** One doc comment per declaration.

## Second recheck

Rechecked at `be2e000f` (PR #1302, open): the two commits after `d63a73d4`, web only, read in full. The design did
not change, so the review was not restarted. Each fix was confirmed against the code and its e2e case; two points
were run with a temporary Playwright probe on the headless project, removed afterwards. Nothing was fixed here.

**Confirmed fixed:** 8, 31, 32, 33 (and with it the rest of 3), 34 to 38, and the rest of 25.

- **8.** Stop carries `agent-entry-stop`, which a card head shows at full opacity beside Open; the e2e asserts the
  computed opacity.
- **31.** A card under hidden lines goes after the hidden run when a shown line follows, as it did before the first
  fix. The other branch was probed: a fold that ends its block, under an empty line, has its row in view, opens, and
  shows no line from past the block.
- **32.** The observer's band reaches far above the tab, so a stop is in it exactly when its heading has passed the
  top-third line, and a jump changes that. The e2e jumps from the last of twelve stops to the top.
- **25.** `ComposeSlash` adds `/btw` only for an agent that can fork, and Send is disabled with its reason when the
  session's slash commands lack it. Add to context still works. No fake agent lacks `/btw`, so this is read, not run.
- **36.** The waiting row falls back to the question's stop when its card has not mounted. Read, not run.

### New bug, from the fixes

**39. A code selection that ends on a blank line offers no actions.** The quote is now the selected text of the
selection's last line, and a selection whose last line has none clears the toolbar (`walkthrough-code.tsx:273`).
That covers the all-blank selection finding 34 was about, and also any selection of code that runs onto a blank
line. The probe selected `a = 1;` and the empty line under it with Shift+Down twice: no Comment button and no Add to
Context, with nothing said. One line shorter, the toolbar shows. Before these fixes that selection could be asked
about. It is small, and the last line with text in the selection would serve as the quote's line.

### New nit

- **40.** `walkthrough-code.tsx:143` calls `options.source(file.after).then(setAfterText)` with no rejection
  handler, so a failed request is an unhandled rejection and the block silently never says "Changed since this
  walkthrough was updated". It needs the `after` request alone to fail, since the block's other two copies are
  awaited first. This line predates the fixes; it was missed in the first review.

## Third fixes

- **39.** The quote and the card's line are the selection's last line with text, so a selection that runs onto
  blank lines is asked about from the line above them; a selection of blank lines alone still offers nothing. The
  empty-line e2e case selects a line and the empty one under it, asks, and checks the quote; it fails without the
  fix, at the missing Comment button.
- **40.** The `after` copy is fetched with the block's other two copies, so a failed request fails the block with
  its notice, as theirs do.

## Third recheck

Rechecked at `572a0bfe` (PR #1302, open): the one commit after `b38a1172`, web only, read in full. The design did
not change, so the review was not restarted. Both fixes were confirmed against the code; the branches the e2e case
does not reach were run with a temporary Playwright probe on the headless project, removed afterwards. Nothing was
fixed here.

**Confirmed fixed:** 39 and 40.

- **39.** The quote's line steps back from the selection's last line to the nearest one with selected text, and
  the card's place uses the same line. The e2e case selects `class Fields {` and the empty line under it and checks
  the quote. Probed beyond it:
  - Two blank lines alone offer nothing, as finding 34 wanted; extending that selection onto text offers Comment.
  - A line and the two blank lines under it quote the line, and the card sits directly under it.
  - A selection through a closed fold onto a blank line quotes the fold's last line, and the card is in view under
    the fold's row. A selection ending at the start of the line after a closed fold already did this, so the fix
    adds no new behaviour here.
- **40.** The `after` copy is awaited with the block's other two, so a failed request fails the block with "This
  code is no longer stored", through the handler `WalkthroughStop.tsx` already has. Read, not run: nothing makes the
  `after` request alone fail.

### New nit

- **41.** `design.md:574` says a block's editor reports its selection with `onDidChangeCursorSelection`, and that a
  question's place is the selection's last line. The code asks once a selection gesture completes
  (`watchSelectionGestures`), and since fix 39 the place and quote are the selection's last line with text. The
  first half dates from the first fixes and was missed in both earlier rechecks.

## Rejected

- **Effects and notes from earlier updates are never cleared.** The design says `effect` is "absent on a stop no
  update touched", so a stop keeps its tag across later updates by design.
- **Collapse has three row types.** The design specifies `block-collapsed` beside `dashboard-collapsed`, "as
  dashboards do".
- **`StageBlockAsync` takes seven parameters.** Six values and a `CancellationToken`, which is within the rule.

## Checked, no finding

- `..` traversal is refused and tested. Symlinks follow `PathBoundary`'s policy, as review hints do.
- Sources are pruned on save and deleted on Replace, `DeleteBlock` and `ClearWorkspace`.
- JSON goes through the source-generated contexts.
- The update and claim path, and `StopAside`'s locking.
- The review-bar change reads correctly, and an end-to-end case now holds a walkthrough tab over a file under
  review.
