# Card tabs — review

Review of [PR #1219](https://github.com/Kapps/weavie/pull/1219) at `0dcd813a` against [design.md](design.md), from
the design and the branch diff. The `weavie-reviewer` agent reviewed the change set; a separate pass read the whole
diff for duplication. Every finding below was checked against the code at that commit. The review fixed nothing;
the fix stage did, and the Status column records the recheck of each fix.

First-pass findings were traced by reading the code; none was reproduced by running the app. B1 is the
exception: it is the PR's own red CI check.

**Verdict at `0dcd813a`:** fix, then ship. Five bugs, twelve warnings, eight nits. Ten candidates were rejected.

**Recheck at `87fb46eb`** ("Fix the card tabs review findings"): every fix holds and none was reopened. One bug
and seven nits are open: B6 (the PR conflicts with main, so CI does not run), N2 to N4 from the first pass, and N9
to N12, which the fixes introduced. The recheck read only what that commit changed and ran, in this worktree: the
CI formatting gate, `tsc`, `biome check` on the changed files, the touched vitest files, the touched C# test
classes, and `card-tabs.spec.ts` plus `acp-background.spec.ts` on the headless project after a fresh `vite build`
and host build. All passed.

**Recheck at `1d01acab`** ("Merge main into card tabs"; `828795e4` only records the first recheck): B6 is fixed
and its conflict resolutions hold. The merge introduced one bug, B7: with the default `agent.progressDisplay`, the
strip's card list throws once earlier replies are folded ahead of a card. Open now: B7 and seven nits (N2 to N4,
N9 to N12). This recheck read only the merge's resolutions and ran `tsc`, all vitest (1,279), and
`card-tabs.spec.ts`, `agent-blocks.spec.ts` and `activity-progress.spec.ts` on the headless project after a fresh
build (16); all passed. B7 was reproduced with a throwaway vitest case, since deleted. CI on `1d01acab` was still
running: `web checks` had passed, and the .NET, e2e and native jobs were pending.

**Recheck at `82532564`** ("Fix the card tabs recheck findings"): every fix holds and none was reopened. B7 and
N2, N4, N9 to N12 are fixed; N3 is fixed in part with the rest rejected and accepted. No bug or warning is open.
One nit is open, N13, which the N2 fix introduced. This recheck read only that commit and ran `tsc`, `biome check`
on its files, all vitest (1,280), `card-tabs.spec.ts` and `agent-blocks.spec.ts` on the headless project after a
fresh build (14), the editor-tab C# tests (17), and the CI formatting gate; all passed. N13 was reproduced with a
throwaway e2e case, since deleted. CI on `82532564` was still queued or pending, and the run on `1d01acab` was
cancelled when this commit was pushed, so the PR has no completed green `ci` run yet.

Severity: **bug** is wrong behaviour a user, an agent or CI hits; **warn** is a standards violation, a design
departure that needs a decision, or duplication; **nit** is minor.

## Bugs

| # | Where | Finding | Status |
| --- | --- | --- | --- |
| B1 | `tests/Weavie.Core.Tests/Mcp/RegistryMcpTests.Blocks.cs:48`, `tests/Weavie.Hosting.Tests/AcpSideCollapseTests.cs:32` | The `.NET tests` check fails to build: local constants `Table` and `Chart` break naming rule IDE1006, so no C# test ran in CI | fixed, confirmed: the constants are `table` and `chart`; `format-dotnet.py --verify-no-changes` exits 0 here. CI has not run on the fix (B6) |
| B2 | `src/web/src/editor/editor-controller.ts:671` | A background open still takes the editor or the keyboard | fixed, confirmed: `openOverlayTab` passes `backgroundOpen`'s options (`editor-controller.ts:671`); the new background-open e2e passes |
| B3 | `src/web/src/agent/agent-cards.ts:44` | A card inside another card never becomes the command target; the outer card always wins | fixed, confirmed: the innermost card claims the press and outer cards skip it (`agent-cards.ts:87-99`); the nested-dashboard e2e passes. It leaves N9 |
| B4 | `src/web/src/agent/AgentAsideReplyBox.tsx:13` | The "opened here" flag survives a sent reply, so the next Reply can lose focus to the other view | fixed, confirmed: an effect clears `openedHere` whenever the reply closes (`AgentAsideReplyBox.tsx:15`); the e2e sends from the tab, then Reply in the card takes focus there |
| B5 | `src/Weavie.Core/Commands/CoreCommands.cs:1141` | `Alt+B` stays captured after a card was pressed, even once the keyboard has moved to the terminal or editor | fixed, confirmed: `When = "agentFocused && agentCardFocused"`, and `evaluateWhen` splits on `&&` (`commands/context.ts:67`). The design's command table was not updated (N12) |

**B2.** `openOverlayTab` calls `openTabFor(session, path, { kind })` and drops the options `openFileFor` passes
from `backgroundOpen` (`editor-controller.ts:507-512`, `:535-541`).

- Editor focused: `activateDestinationFor` returns false, but `openTab` still commits the new tab as active
  (`session-store.ts:108-109`), so the dashboard replaces the file being typed in.
- Editor not focused: the tab is presented with a placement that has no `focus: false`, so `applyActive`
  focuses it (`editor-controller.ts:472-473`) and the keyboard leaves the composer.

This is the path of `openDashboard` without `focus` and of `openCard` with `background: true`. The design says a
background open "never takes the editor from what the user is doing in it". No test covers it.

**B3.** A dashboard inside a `/btw` is one `AgentCardShell` inside another, both with the handler from
`ownCardOnFocus`. The press reaches the dashboard first, then the `/btw`, whose handler sees it is not the owner
and takes ownership, releasing the dashboard (`agent-command-owner.ts:51`). `Alt+B` and the palette's *Open Card
in Tab* then act on the `/btw`. The deleted `toggleAgentAside` picked the innermost card.

**B4.** `openedHere` resets only in the box's own `close()`. A sent reply closes the box through
`composer-state.ts:331` without resetting it. Reply in the tab, send, then Reply in the chat card: both boxes
mount with `autofocus` true. Which one ends up focused depends on mount order, which was not run. The e2e covers
the first open only.

**B5.** The binding takes the command's `When`, now `agentCardFocused` (was `agentFocused`). That context is
released by a press outside the card, the next owner, or unmount, never by a focus move
(`agent-command-owner.ts:8-12`), and bindings resolve ahead of the terminal and Monaco
(`commands/keybindings.ts:1`). Press a card, move to the terminal by keyboard, press `Alt+B`: the card toggles and
the shell never sees the key. The code follows the design's command table, so the fix is a design decision.

## Warnings

| # | Where | Finding | Status |
| --- | --- | --- | --- |
| W1 | `src/web/src/App.tsx:1638` | `openCard` casts its arguments and never checks them or the card | fixed, confirmed: `namedCard` validates the arguments and `liveCard` refuses a card that is gone; `agent-cards.test.ts` passes. It leaves N11 |
| W2 | `src/web/src/App.tsx:1624` | `toggleAside` naming a card without `collapsed` toggles the focused card instead | fixed, confirmed: any card argument names a card, which then needs `kind` and a boolean `collapsed`; a half-named card throws |
| W3 | `src/web/src/agent/AgentBlockEntry.tsx:30` | A lone block inside a `/btw` has no Open and never owns the card target | fixed, confirmed: a block entry carries its `dashboardId`, and Open shows for any block outside a dashboard in a transcript (`AgentBlockEntry.tsx:31`). It leaves N9 |
| W4 | `src/web/src/agent/AgentStrip.tsx:134` | The strip's ✕ advertises `Alt+B`, which does not collapse that card | fixed, confirmed: ✕ runs `toggleAside` with its card named, and its tooltip is the command title with no shortcut |
| W5 | `src/web/e2e/functional/card-tabs.spec.ts:1` | Much of the design's Testing table is absent | fixed in part, confirmed: restart, `/clear`, Rewind past the content, `openDashboard` on an unbound grant, named cards, and e2e for the gone notice, background and focused opens, nested ownership and reply focus were added and pass. Rejected by the fix stage for the rest (swapped parallel results, one transaction, ledger and store cases, held history, shared sort): no logic of their own in this change. Accepted; `ownTarget` still has no unit test and is covered only by the nested e2e |
| W6 | `src/web/src/agent/agent.css:269` | `.agent-aside-toggle` is a button styled from scratch with `font: inherit` | fixed, confirmed: the toggle is a `.ui-row` with its padding variables zeroed and no font or button reset. It leaves N10 |
| W7 | `src/web/src/agent/AgentCardShell.tsx:36` | Duplication: the Open button three times, its tooltip text four times | fixed, confirmed: one `AgentCardOpenButton` and one `openCardTitle()`, used at all four sites |
| W8 | `src/web/src/agent/AgentBlocksTab.tsx:44` | Duplication: the two tab components repeat their frame and gone notice | fixed, confirmed: `AgentCardTabFrame` holds the host element, binding, lookup and gone notice for both tabs |
| W9 | `src/web/src/agent/AgentPaneBlocks.ts:102` | Duplication: `bare()` restates the base entry that `entry()` builds | fixed, confirmed: `entry()` lives in `AgentPaneEntry.ts` and both projections build on it; `bare()` is gone |
| W10 | `src/web/src/agent/agent-cards.ts:25` | Duplication: a second card reference shape, built by hand at four call sites | fixed, confirmed: `setCardCollapsed` takes the card reference; every caller passes one |
| W11 | `src/web/src/editor/editor-controller.ts:652` | Duplication: `openWebTab` and `openSourceTab` were not folded into `openOverlayTab` | fixed, confirmed: `openWebTab` and `openSourceTab` call `openOverlayTab` |
| W12 | `tests/Weavie.Hosting.Tests/AcpDashboardTests.cs:94` | Duplication: test helpers and fixtures copied between test classes | fixed, confirmed: `TurnAsync` and `Rows` are fixture methods, and `AcpSideCollapseTests` uses `AcpDashboardTests.Chart` |

**W1.** With no owned card, `{}` or `{ background: true }` reaches `openAgentTab(session, undefined, undefined, …)`.
`{ kind: "dashboard" }` opens and saves a tab at `dashboard:undefined@`. A well-formed reference to a card that is
gone opens a tab; the design's error table says the command fails with "That card is no longer in this
conversation." The handler this replaces checked its argument's type.

**W2.** The explicit branch runs only when `collapsed` is a boolean. `{ dashboardId: "rollout" }` alone falls
through to the owned target and toggles whichever card was last pressed.

**W3.** `row.rowId === props.entry.id` stands for "not in a dashboard", but only `AgentTranscript.tsx:105`
provides the row context, so inside a `/btw` the row id is the `/btw`'s. The `block:<id>@<conversationId>` path
and `model.block(conversationId, …)` exist for this case and are unreachable from the UI.

**W4.** The tooltip is "Collapse Card" plus the `toggleAside` shortcut. A press in the strip releases card
ownership, so `Alt+B` then does nothing; reached by keyboard, `Alt+B` toggles the last-pressed card, which may be
another one. The button calls `setCardCollapsed` directly, where the design has it run `toggleAside` with the
explicit reference. "Collapse Card" and the added line's "Show Dashboard"
(`AgentBlockAddedEntry.tsx:14`) are not command names.

**W5.** Missing against the Testing table:

- Hosting: restart, `/clear`, one transaction, parallel shows with swapped results, Rewind past the content,
  collapse after the cut. `AcpBlockTests` and `AcpRewindTests` are untouched; the new cases sit in
  `AcpDashboardTests`.
- Core: `openDashboard` on an unbound grant, the `AcpSessionStoreTests` cases, the `AgentPaneLedgerTests` cases.
- Vitest: `ownTarget` (it would have caught B3), the nested projection, `AgentPaneLayout`. The tab-path test sits
  in `background-tab.test.ts`.
- e2e: the later line scrolling to its card, a held history response, `/clear`, the gone notice after Rewind,
  shared table sort, opening a subagent, any background or focus open (B2). `agent-blocks.spec.ts` is untouched.

**W6.** AGENTS.md: "never restyle a button… never set a font on a control". The old `.agent-aside-head` button
carried the same debt; this change moves it into a new rule.

**W7.** The same `agent-entry-rewind agent-entry-open` button with "Open" is written in `AgentCardShell.tsx:36`,
`AgentBlockEntry.tsx:97` and `AgentSubagentEntry.tsx:55`. The string "Open Card in Tab" plus a key hint is at
those three and `AgentStrip.tsx:124`, once with `keyHint` and three times with `liveKeyHint`. It restates the
title in `CoreCommands.cs`.

**W8.** `AgentBlocksTab.tsx:44-60` and `AgentConversationTab.tsx:24-31` repeat the `editor-agent-view` host
element, its `bind` wiring, the `cardForTabEntry` memo, and the "no longer in the transcript" notice shown once
history is complete.

**W9.** `bare()` lists the same nine fields as `entry()` in `AgentPaneMessages.ts:270-291`.

**W10.** `setCardCollapsed` takes `{ conversationId, dashboardId }`, built by hand in `AgentAsideEntry.tsx:24`,
`AgentDashboardEntry.tsx:22`, `AgentStrip.tsx:136-142` and `App.tsx:1626-1629`. `AgentCardRef` and `cardOf(entry)`
already identify the same card.

**W11.** The design says the three fold into one function. `editor-controller.ts:652-668` still holds two more
copies of activate, open, present.

**W12.** `CallAsync(fixture, prompt, turn)` is copied from `AcpBlockTests.cs:232`. `Rows(fixture)` (`:91`) repeats
the read at `AcpBlockTests.cs:26`, `:116` and `AcpAgentSessionFixture.cs:558`. The dashboard chart JSON is in
both `AcpDashboardTests.cs:8` and `AcpSideCollapseTests.cs:32`.

## Nits

| # | Where | Finding | Status |
| --- | --- | --- | --- |
| N1 | `src/web/src/agent/AgentBlockAddedEntry.tsx:16` | The "added to…" line is a dead button in a `/btw` tab (no row context) and before history completes | fixed, confirmed: without a transcript row the line is plain text; before history completes the button scrolls to its own row |
| N2 | `src/web/src/agent/AgentBlocksTab.tsx:84` | A table never sized by the user gets a fixed 310px frame in a tab, against the comment beside it; the design's `--agent-table-max-h` variable was not used | fixed, confirmed: an unsized table passes no height in a tab and only a sized block (`data-sized`) fills its frame; the e2e compares the table's height in the chat and the tab and passes. It leaves N13 |
| N3 | `src/web/src/agent/AgentBlocksTab.tsx:97` | The height handle is a `<button>` titled "Resize", not the design's `role="separator"`; its ArrowUp and ArrowDown keys are not advertised | fixed in part, confirmed: the tooltip names the drag and ArrowUp/ArrowDown. `role="separator"` rejected by the fix stage: Biome's a11y rules want an `<hr>` with value props, and the named, focusable button gives the same keyboard path. Accepted |
| N4 | `src/Weavie.Hosting/HostSession.EditorState.cs:135` | On a Windows host tab paths compare without case, so dashboards `Rollout` and `rollout` share one tab | fixed, confirmed: `HostSession.IsTab` compares a file path by the OS rule and any other tab's path exactly (`HostSession.EditorState.cs:138`); `EditorTabPathTests` passes |
| N5 | `docs/specs/card-tabs/design.md:4` | The status line still says "Nothing is built yet". `AgentPaneMessage.cs:77`'s comment and `docs/specs/acp-rich-content/design.md` were not updated as the Files table lists | fixed, confirmed: the status line, the `ParentItemId` comment, and a pointer from the rich-content design |
| N6 | `src/web/src/agent/AgentBlocksTab.tsx:10` | Duplication: the `BlockEntry` type is declared here, in `AgentDashboardEntry.tsx:9`, and inline in `AgentBlockEntry.tsx` | fixed, confirmed: `AgentBlockTranscriptEntry`, declared once |
| N7 | `src/web/src/agent/AgentPaneBlocks.ts:62` | Duplication: the `conversationId` spread three times (`:62`, `:83`, `:98`); the "updated HH:MM" format in `AgentDashboardEntry.tsx:62` and `AgentBlockEntry.tsx:111` | fixed, confirmed: `inConversation()` and `blockUpdatedAt()` |
| N8 | `src/web/src/editor/editor-controller.ts:181` | Duplication: the card kinds are listed again here, in `TabStrip.tsx:48`, `tab-entry.ts` and the two tab components' props | fixed in part, confirmed: `AgentCardKind` names the kinds for the opener; the tab strip keeps its names map, which needs one name per kind. Accepted |

## Recheck: new findings

| # | Severity | Where | Finding | Status |
| --- | --- | --- | --- | --- |
| B6 | bug | PR #1219 | The PR conflicts with main since #1218 merged, so the `ci` workflow does not run on `87fb46eb` and no fix is CI-verified | fixed, confirmed: `origin/main` (`62cbd6dc`) is merged and the PR is mergeable. `AgentSubagentTab.tsx` stays deleted with main's `onUpdatesToggle` carried into `AgentConversationTab`; main's `turnKey` is in the shared `entry()`; `AgentTranscript` keeps the row context and `onUpdatesToggle`. The `ci` workflow runs on the merge. The merge leaves B7 |
| B7 | bug | `src/web/src/agent/AgentUpdatePresentation.ts:55` | With the default `agent.progressDisplay` (`latest`), the strip's card list reads stale indexes once earlier replies are folded, and throws or lists the wrong entry | fixed, confirmed: `presentUpdates` recomputes `cardIndexes` from the entries it shows (`AgentUpdatePresentation.ts:63`); the new `AgentPaneBlocks.test.ts` case folds four replies ahead of a dashboard with `latest` and reads `cards()`, and passes |
| N9 | nit | `src/web/src/agent/AgentBlockEntry.tsx:62` | Pressing a chart or table inside a `/btw` makes `Alt+B` do nothing; before the fixes it collapsed the `/btw` | fixed, confirmed: a lone block's toggle is the enclosing card's, through `AgentCardToggleContext`; the e2e collapses a BTW with `Alt+B` from a chart inside it and passes |
| N10 | nit | `src/web/src/agent/agent.css:270` | A long card title is now cut off with no ellipsis | fixed, confirmed by reading: the label is a span that is the flex item, with `overflow: hidden` and `text-overflow: ellipsis` (`agent.css:278`). Not run |
| N11 | nit | `src/web/src/App.tsx:179` | `liveCard` is declared between the import statements of `App.tsx` | fixed, confirmed: `liveCard` is in `card-tab.ts` beside `cardEntry` |
| N12 | nit | `docs/specs/card-tabs/design.md:435` | The design's command table still gives `toggleAside` the old `When` and arguments | fixed, confirmed: the command row gives `agentFocused && agentCardFocused`, the card-kind arguments, and the lone-block rule |
| N13 | nit | `src/web/src/agent/AgentBlockEntry.tsx:31` | A lone table's own tab now shows an Open button, which reopens the tab it is in | fixed: Open shows only under a transcript row of the pane (`AgentRowContext`), which a tab has none of; the lone-table e2e asserts the tab has no Open |

**B6.** `gh pr view` reports `CONFLICTING`. A trial merge of `origin/main` (`62cbd6dc`) conflicts in
`AgentConversationProjection.ts`, `AgentPaneMessages.ts`, `AgentTranscript.tsx`, and `AgentSubagentTab.tsx`
(deleted here, modified on main). Only the CodeQL checks ran on the fix commit. The fixes did not cause this, but
it blocks the merge, and resolving it changes the projection files this PR edits, so the suite needs a run after.

**B7.** Introduced by the merge. Main's `presentUpdates` builds the shown snapshot as `{ ...source, entries,
entryIndexes, … }` and remaps `pendingRowIndexes`, but it does not know this PR's `cardIndexes`, which keep the
unfolded positions. `model.cards()` (`AgentPaneModel.ts:275-281`) reads `snapshot.entries[index]` through them.

- Reproduced: a turn with four replies, then a turn that creates a dashboard. Shown entries are
  `message, updates, message, message, dashboard` with `cardIndexes: [6]`, and `cards()` throws
  `TypeError: Cannot read properties of undefined (reading 'asideCollapsed')`. With `progressDisplay` `all` it
  returns the dashboard.
- `cards()` runs in `AgentPaneBody`'s effect and feeds `AgentStrip`, so the throw is in the agent pane's render
  path. That step was read, not run in the app. When the stale index is still in range, the strip lists an
  entry that is not a card.
- A `/btw` is in `cardIndexes` too, so any session with a `/btw` or dashboard after a folded turn is affected.
- No test covers it: the merge's resolution of `AgentPaneBlocks.test.ts` passes `"all"` for the progress display,
  and the e2e fake agent replies once per turn, so CI stays green.

**N9.** With W3 fixed a lone block in a `/btw` owns the card target, and with B3 fixed it claims the press ahead of
the `/btw`. Its `toggle` is null, so the `toggleAside` handler returns false. *Open Card in Tab* correctly opens
the block. Traced, not run.

**N10.** `.ui-row` makes the toggle a flex container (`primitives.css:6`), and `text-overflow` does not apply to
the anonymous flex item holding the label, so `overflow: hidden` clips it. Traced from the CSS, not run.

**N11.** Biome accepts it, but the function sits inside the import block of a 2,300-line file; `card-tab.ts`
already holds `cardEntry`, which it wraps.

**N12.** The code is `When = "agentFocused && agentCardFocused"` with arguments
`{ kind, conversationId, dashboardId?, collapsed }`. The design's status line points here for what review changed.

**N13.** `AgentBlockEntry` shows Open when `dashboardId` is undefined and `height` is null, taking a null height
to mean "in a transcript". With N2 fixed, `BlockRow` passes null for a table the user has not sized, so the tab
of a lone table shows Open. Reproduced: the table's tab has one Open button and a lone chart's tab has none. The
value is read once as the component is created, so the button stays after the table is resized.

## Rejected

| Candidate | Reason |
| --- | --- |
| Two parallel shows can both create the dashboard row | `ClaimBlock` runs inside `_turnTransitionGate` (`AcpConversation.Tools.cs:11`), the lock the port takes, so lookup and commit are one critical section |
| A dashboard created again after a refused turn lands at the refused turn's place | The ledger's item key includes the turn id (`AgentPaneIdentity.cs:10-13`), so the new row is appended |
| `When = agentCardFocused` blocks the strip's and an agent's explicit-argument runs | `when` gates only key bindings, the palette and the menu, never a run |
| A staged show that is never claimed makes later results say "Added" forever | Unclaimed operations are drained (`AcpConversation.Blocks.cs:51`, `CapabilityRegistryGrant.cs:62`) |
| The strip rebuilds its pills on every flush because `cards` is not a memo | A `/btw`'s entry is one stable object per conversation, and `share()` reuses deep-equal dashboard entries |
| `openDashboard` on an unbound grant throws unhandled | `McpServer.cs:357` turns `ToolUnavailableException` into a tool error |
| e2e publishers of `openOverlay` lack the intent; `mock-host.ts` is untouched | Both publishers pass it; the mock host forwards the payload |
| `openDashboard` should be a command, not a tool | Design decision 4: only a tool call knows the calling conversation |
| The two handlers grow `App.tsx`, already far past 300 lines | The design's Files table places them there |
| `restore` does not apply a saved view state as the design says | The signal is seeded from the entry at mount, which covers reload and reactivation |
