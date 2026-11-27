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

Severity: a **bug** is wrong behaviour a user or another feature would hit; a **warn** is a standards or
maintainability problem; a **nit** is anything smaller.

| # | Severity | Status | Finding | Where |
| --- | --- | --- | --- | --- |
| 1 | bug | open | A note, fold row or question under a block's last line is not drawn | `src/web/src/agent/walkthrough/walkthrough-code.tsx:225` |
| 2 | bug | open | A stop row on the pane card always opens the tab at stop 1 | `src/web/src/agent/walkthrough/WalkthroughTab.tsx:275` |
| 3 | bug | open | Two blocks on one deleted file collide, and any failure to open a file reads "Deleted" | `src/web/src/agent/walkthrough/walkthrough-code.tsx:70` |
| 4 | bug | open | A question asked from a walkthrough a `/btw` published is refused as "no longer shown" | `src/Weavie.AgentClientProtocol/AcpAgentSession.SideConversations.cs:28` |
| 5 | bug | open | A dismissed question that then needs a permission can't be answered | `src/web/src/agent/walkthrough/WalkthroughCard.tsx:33` |
| 6 | bug | open | A whole-line selection puts its question one line low | `src/web/src/agent/walkthrough/walkthrough-code.tsx:255` |
| 7 | bug | open | After an update re-records a block, older questions sit under the wrong line | `src/web/src/agent/walkthrough/WalkthroughTab.tsx:119` |
| 8 | bug | open | `/btw` cards did not get Stop | `src/web/src/agent/AgentAsideEntry.tsx:48` |
| 9 | warn | open | Scrolling past a stop, opening a fold or revealing an answer repaints every mounted block | `src/web/src/agent/walkthrough/WalkthroughTab.tsx:214` |
| 10 | warn | open | Code selection is a second copy of the editor's selection actions and works on every selection event | `src/web/src/agent/walkthrough/walkthrough-code.tsx:248` |
| 11 | warn | open | `WalkthroughQuestion` re-wires `AsideEntry` | `src/web/src/agent/walkthrough/WalkthroughQuestion.tsx:30` |
| 12 | warn | open | `ReviewHintStore` hashes and splits text `WorktreeTextFile` already hashed and split | `src/Weavie.Core/Review/Hints/ReviewHintStore.cs:86` |
| 13 | warn | open | Clicking a question's head dismisses it for good, under a "Collapse" tooltip | `src/web/src/agent/walkthrough/WalkthroughQuestion.tsx:40` |
| 14 | warn | open | Stop is a bare bus message, not a command | `src/web/src/agent/walkthrough/walkthrough-ask.ts:34` |
| 15 | warn | open | The host checks neither the anchor's place nor its quote | `src/Weavie.Hosting/HostCore.Sessions.cs:81` |
| 16 | warn | open | The fold row and strip buttons are styled from scratch, with a font set | `src/web/src/agent/walkthrough/walkthrough.css:162` |
| 17 | warn | open | `WalkthroughTab.tsx` is 417 lines doing six jobs | `src/web/src/agent/walkthrough/WalkthroughTab.tsx:83` |
| 18 | warn | open | `McpServer` holds a nullable `WalkthroughCapture` | `src/Weavie.Core/Mcp/McpServer.cs:43` |
| 19 | warn | open | Tests the design lists are absent | `src/web/e2e/functional/walkthroughs.spec.ts:83` |
| 20 | nit | open | A block shows one notice; "Recorded after edits" hides the others for good | `src/web/src/agent/walkthrough/walkthrough-code.tsx:177` |
| 21 | nit | open | A failed source request shows the raw error and is cached for the tab's life | `src/web/src/agent/walkthrough/WalkthroughStop.tsx:213` |
| 22 | nit | open | An update can blank `title`, `problem` or `gist` with an empty string | `src/Weavie.Core/Agents/AgentWalkthroughRules.cs:56` |
| 23 | nit | open | An update accepts `title`, which the design does not list; the pane card keeps the old one | `src/web/src/agent/walkthrough/WalkthroughCard.tsx:45` |
| 24 | nit | open | The 2 MB limit is checked after the file is read, split and hashed | `src/Weavie.Core/Agents/WalkthroughCapture.cs:33` |
| 25 | nit | open | Three small design items are not built | `src/web/src/agent/walkthrough/WalkthroughTab.tsx:150` |
| 26 | nit | open | `Prose` builds a new cache key per read, so its markdown is never cached | `src/web/src/agent/walkthrough/WalkthroughStop.tsx:32` |
| 27 | nit | open | Three-line file comment | `src/web/src/agent/walkthrough/walkthrough-code.tsx:1` |
| 28 | nit | open | `em` in chrome geometry | `src/web/src/agent/walkthrough/walkthrough.css:107` |
| 29 | nit | open | A stop request is keyed by tab path alone, in a module global | `src/web/src/agent/walkthrough/walkthrough-view.ts:19` |
| 30 | nit | open | The embedded-editor setup repeats the review's, with a class that has no rule | `src/web/src/agent/walkthrough/walkthrough-code.tsx:78` |

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
- The review-bar change reads correctly; it has no end-to-end case.
