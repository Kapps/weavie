# Card tabs — challenge

**Status:** first challenge to [brief.md](brief.md). Evidence is the code in this worktree, the two mockups, and
an independent read by the `weavie-architect` agent whose citations were checked line by line. Nothing was built
or run; each unverified link is marked.

**Verdict.** The product shape holds: a group the agent names, a live card, the same content at editor width.
The approach under it is weak in four places. A dashboard is not an object anywhere, so nothing that must find
it again can. Blocks leave the turn that made them. The strip takes over a list whose meaning is "work that
runs". "One card tab" names an abstraction the code does not have. A better approach exists for each, and none
changes the agreed card, strip, or tab.

## 1. A dashboard has no identity and no owner

*What the user gets:* after a reload the charts appear loose and then jump into a card; a `/clear` followed by
a new "rollout" dashboard stays hidden from the strip because the old one was dismissed; "Rollout" and
"rollout" are two dashboards and nobody is told.

The brief defines a dashboard as "every block with the same name in one conversation", gathered by the
transcript. That is a derived view. Five things in the brief must address it durably: the tab restored after
restart, the dismissal that survives restart, collapse, the strip entry, and the agent's "open this card".

**What the code has.**

- A block's whole identity is `(providerId, conversationId, blockId)`; there is no container
  (`src/Weavie.Core/Agents/ConversationOutput.cs:17`, `src/web/src/agent/AgentPaneIdentity.ts:36-42`).
- The main chat's conversation id is null, stored as the empty string
  (`src/Weavie.Core/Sessions/AcpSessionStore.Blocks.cs:77-80`), and it stays null across `/clear` and Rewind.
  Only an in-memory generation counter tells one transcript from the next
  (`ConversationOutput.cs:121-125`). So the main chat's "rollout" has the same name-derived key before and
  after `/clear`.
- Every card that exists today is placed by a journaled marker row keyed by its own conversation id
  (`src/web/src/agent/AgentPaneConversationRows.ts:49-57`), and its tab path is that id
  (`src/web/src/editor/tab-entry.ts:14-17`).
- The transcript is projected from rows alone. A block row carries the block id, kind and title and nothing
  else (`src/Weavie.AgentClientProtocol/AcpConversation.Blocks.cs:24-33`,
  `src/web/src/agent/AgentPaneMessages.ts:175-190`).
- Block state travels separately and later: live as its own `block` push after the row
  (`ConversationOutput.cs:46-47`, `src/Weavie.Hosting/Agents/AgentPaneOutput.cs:146`), and on reload only with
  the final history batch (`src/Weavie.Hosting/Agents/AgentPaneProtocol.cs:20-36`,
  `src/web/src/agent/pane-store.ts:127-129`). History arrives newest first in batches of 64 and the first
  batch is painted before the rest (`AgentPaneProtocol.cs:21-25`,
  `src/web/src/agent/AgentPaneAccumulator.ts:110-112`).

**Consequences of "a group name, saved with the block's latest state".**

- **Placement waits for data that is not there yet.** A card cannot form until block records land. On every
  reload the grouped rows either show loose and then collapse into a card, or stay hidden until history
  completes. The rich-content brief promised the opposite: a block row's height is known before its data, "so
  scrolling never shifts when data lands" (`docs/specs/acp-rich-content/brief.md:57-58`).
- **"The place its first block was shown" is wrong in a partial view.** The newest batch can hold a
  dashboard's fifth block without its first, so the card is drawn at one place and then moves.
- **A dismissal keyed by name outlives its dashboard.** After `/clear` the next "rollout" inherits it unless a
  second owner remembers to clear it. Nothing on the host knows a dashboard exists, so nothing there can.
- **A moved block keeps its group across Rewind.** Block state survives a Rewind by design
  (`docs/specs/acp-rich-content/design.md:625`). If `updateBlock` may set the group, a block rewound to before
  the move stays in the card.
- **A wrong name cannot be refused.** `showBlock` and `updateBlock` refuse a wrong block id
  (`src/Weavie.Core/Agents/AgentBlockRules.cs:59,65-67`). A group name has nothing to be checked against, and
  it is also the header text, so it cannot be renamed.
- **The agent's "open" cannot say which dashboard it means.** `runCommand` carries no calling conversation
  (`src/Weavie.Core/Mcp/McpServer.cs:661`), and *Open Background Work* runs in the web against the command
  context's session (`src/web/src/App.tsx:1625-1633`). The same name in the main chat and in a `/btw` is two
  dashboards by the brief's own edge case, and a `/btw` starts as a fork of the main context
  (`src/Weavie.AgentClientProtocol/AcpAgentSession.SideConversations.cs:28`), so it believes it owns the name.
  The block tools do know their caller (`src/Weavie.Core/Mcp/McpServer.BlockTools.cs:94,104`).

**Missing abstraction.** A dashboard is a card, and every other card is a journaled, id-bearing thing its
conversation owns. Give the dashboard the same: the owning conversation journals a dashboard marker row (id
under the block id rule, title) when the first block names it, and stamps each member's reference row with that
id. Then placement comes from rows alone, the tab path and the strip entry are that id, collapse and dismissal
are rows on it exactly as `/btw` collapse is (`AcpAgentSession.SideConversations.cs:57-65`), and `/clear` and
Rewind remove all of it with the journal prefix (`src/Weavie.AgentClientProtocol/AcpRewindPlan.cs:28-31`) with
no second owner. The agent still names the group in one call. The tool result can say "created dashboard X" or
"added to dashboard X (5 blocks)", so a typo is visible to the model.

## 2. Later blocks leave the turn that made them

*What the user gets:* asking "also add error rate" produces a turn with nothing in it; the chart appears in a
card that may be far off screen. Rewinding to turn 3 removes a chart from a card drawn at turn 1.

"A block added to a dashboard later appears inside the card and leaves no row where the agent added it"
reverses a shipped decision: "Blocks stay at their place in the transcript" and "each block lands once, on the
row of the call that made it" (`docs/specs/acp-rich-content/brief.md:30,59`).

- **The row cannot actually go.** A block lives only while its reference row does: retracting the row deletes
  the block, and a restore keeps only blocks whose rows remain (`ConversationOutput.cs:31-35`,
  `AcpSessionStore.Blocks.cs:31-36,41-54`). So "leaves no row" can only mean "the web hides a row the journal
  keeps". The brief does not say so, and removing the row on the host silently deletes the chart at the next
  restore.
- **Rewind stops matching what is on screen.** Rewind keeps a journal prefix (`AcpRewindPlan.cs:28-31`). With
  hidden rows, "everything below this prompt goes" also removes a chart drawn above the prompt. The brief
  accepts the outcome ("keeps the card with the rest") without saying the user can no longer see why.
- **A reader following at the bottom gets no signal.** New output is noted only when the transcript's last
  entry changes (`src/web/src/agent/AgentPaneModel.ts:136`). A hidden row changes nothing there, so a new
  chart arrives with no row and no "new output" mark.

**Better.** The block renders in the card, and its reference row stays visible at its turn as one line
("Error rate · added to Checkout rollout") that reveals the card. Order, Rewind, and the new-output mark all
keep working with no special case. Cost: one short row per added block, where the brief wanted none.

How turn review treats a hidden row was not traced.

## 3. The strip takes over a list that means "work that runs"

*What the user gets:* every `/btw` ever asked sits above the composer until dismissed by hand, and a running
subagent with its Stop button is pushed off the right edge. The mockup already clips `Stop` with three entries
(`mockups/strip.png`).

**What the tray is today.** A host-owned list of subagents, tasks, and self-started turns, pushed whole
(`src/web/src/agent/agent-background-store.ts:6-23,28-34`), cleared by the host at the next prompt
(`src/Weavie.AgentClientProtocol/AcpBackgroundWork.cs:103-112`, `docs/specs/background-work.md:57`). The same
list answers "does stopping this session lose work" (`src/Weavie.Hosting/HostCore.AgentBackground.cs:27-30`).
It hides when empty and scrolls sideways when full (`src/web/src/agent/AgentBackgroundTray.tsx:25,48,57-62`).

- **Dashboards and `/btw`s cannot join that list**, since it feeds the stop guards. The strip becomes a union
  built in the web from four sources: host background items, transcript cards, an "opened once" set for lone
  blocks, minus a dismissed set. Two of those four have no home that survives restart. Per-session state that
  survives restart lives in the editor session or the journal
  (`src/Weavie.Core/Editor/EditorSessionModel.cs:45-58`, `AcpAgentSession.SideConversations.cs:57-65`); the
  brief names neither.
- **It is a rule table, not a rule.**

  | Kind | Listed | ✕ | Clears itself | Stop |
  | --- | --- | --- | --- | --- |
  | Running subagent or task | always | no | no | tasks |
  | Finished subagent or task | always | yes | next prompt | no |
  | Dashboard, `/btw` | always | yes | never | no |
  | Lone block | once opened | yes | never | no |

- **An attention surface becomes a bookmark bar.** Today everything listed is running or just finished. With
  entries that never clear, the one thing that needs attention is one pill among stale ones. On first load an
  existing session lists every `/btw` it ever had.
- **"Already reachable from the keyboard" holds only while work runs.** *Show Background Work* is gated on
  `agentBackgroundActive` (`src/Weavie.Core/Commands/CoreCommands.cs:1184`,
  `src/web/src/agent/AgentPaneBody.tsx:22-27`). The gate, the title, the aria label, and the command names all
  say "background work".
- **The implicit target gets worse.** Open and Stop pick "the most recently started" item when given no id
  (`src/web/src/agent/background-tab.ts:15-20`, `HostCore.AgentBackground.cs:13-16`). A dashboard has no start
  time. The mixed list should drop the default, not extend it. ✕ is a new action and needs its own command
  with an explicit id.

**Better.** Keep two groups in one strip: running work first with today's rules, then cards. A card is listed
because its marker row exists and unlisted by a journaled row on its id, the mechanism `/btw` collapse already
uses, so restart, `/clear`, and Rewind need no extra code. Decide whether a `/btw` is listed by default or only
while it is active or awaiting a reply; listing all of them forever is the weakest rule in the table.

## 4. "One card tab" is not one tab

The brief says "the tab that shows a subagent or a task today becomes the tab for any card, rendering the
card's own content components".

- There are two tab kinds and two components, not one (`src/web/src/editor/session-types.ts:13`,
  `src/web/src/editor/TabContent.tsx:133-138`). The shared part, opening a session-owned agent tab, already
  exists (`src/web/src/editor/editor-controller.ts:663-666`).
- **A task is not a card and its tab is not its card's content.** A task tab reads the host's in-memory list,
  shows facts the chat never shows, and says "no longer listed" once the list clears
  (`src/web/src/agent/AgentTaskTab.tsx:17-21,29`). Only workflow tasks have a chat card at all
  (`src/Weavie.AgentClientProtocol/AcpBackgroundWork.Tasks.cs:144-145,149-151`). "A tab restored after reload
  or restart shows the card's latest state" is false for tasks today.
- **The subagent tab does not render the card's component.** It rebuilds the card's header in its own markup
  and shares only the nested transcript (`src/web/src/agent/AgentSubagentTab.tsx:33-58`).
- **One `card` kind hides the discriminator.** Each kind still needs its own content, title, and "gone" rule,
  so a single kind moves a typed field into path-prefix parsing.

**The real shared thing is narrower.** A `/btw` and a subagent are both nested conversations in the web model,
and the lookup the subagent tab uses resolves either (`src/web/src/agent/subagent-tab.ts:7-15`,
`src/web/src/agent/AgentPaneModel.ts:263-265`). So: one *conversation* tab for subagent and `/btw` (the `/btw`
adds its reply box), one *blocks* tab for a dashboard or a lone block, and the task tab left alone. That is
three kinds behind the one existing opener, and it is less code than a tab that switches on five.

## 5. The chat card repeats the tab at full size

*What the user gets:* a five-block dashboard is a card about 1,500 px tall in a pane the mockup shows at about
700 px (`mockups/card-tab.png`), and it grows 300 px per added chart.

- A chart's frame is a fixed 300 px "so an update never moves the transcript"
  (`src/web/src/agent/agent-block-layout.ts:1-4`), and a block row's estimate is exact for that reason
  (`src/web/src/agent/AgentPaneEstimate.ts:21-22`). A card of N blocks is one virtual row whose height changes
  whenever the agent adds one.
- **The brief's edge case names the free direction.** The pane is bottom-origin, so "growth above the reader
  never moves it" (`src/web/src/agent/agent.css:145-149`). The case that costs is the reader *above* the card,
  or inside it, when it grows. That relies on the reading anchor's scroll writes
  (`src/web/src/agent/AgentPaneAnchor.ts:3-6`). Whether those hold mid-gesture on iOS is unverified here; the
  brief should name this case and test it on a phone.
- **Both mounts at once is the normal case**, as the mockup shows, and some state assumes one mount:
  - Table sort and filters are keyed by transcript row id, so the card and the tab share them
    (`src/web/src/agent/block-store.ts:75-86`, `src/web/src/agent/AgentBlockEntry.tsx:23-24`). Decide if
    sorting in the tab should re-sort the chat.
  - The row kept mounted during a selection gesture is found by top-level entry id
    (`src/web/src/agent/AgentPaneLayout.ts:84-94`). A block inside a card is not a top-level entry, so a card
    that scrolls out mid-drag unmounts under the pointer.
  - Selections always go to the main composer (`AgentBlockEntry.tsx:26-27`,
    `src/web/src/agent/composer-store.ts:30-31`). In a `/btw` tab with its own reply box beside the chart,
    "exactly as selecting in the chat does" sends the chip to another pane.
  - The reply box takes focus when it mounts, and "open" is shared state
    (`src/web/src/agent/AgentAsideReply.tsx:34-36`, `src/web/src/agent/composer-state.ts:42`). With both
    mounted, the chat card scrolling into view steals focus from the tab.
  - The collapse command finds its target by querying the agent pane's DOM for the focused or newest visible
    card (`src/web/src/agent/AgentNestedCard.tsx:11-22`). It cannot reach a card shown in a tab, and adding
    dashboards extends a command that guesses its target.

**Alternative.** The chat card is compact (title, counts, Open) and the tab is the only full-size home. That
removes the tall mutable row and every dual-mount catch for dashboards. It loses the agreed "charts stay full
size in the chat", and a dashboard nobody opens shows no data. A middle ground is expanded while its turn is
the latest, compact after. This is a product call; the brief should make it knowing the costs above.

## 6. The tab's stated reason is narrower than it reads

"Watching and developing rarely share a session" is true, and it means the dashboard is visible only while the
watching session is selected: only the selected session's active tab is mounted
(`src/web/src/editor/TabContent.tsx:175-189`). What the tab adds is width inside that session. Other
destinations are out of scope, so the brief should state the limit: a rollout cannot be watched while working
in another session.

A tab away from the chat also raises something the rich-content brief deferred, "an overdue marker for stale
blocks" (`docs/specs/acp-rich-content/brief.md:70`). In the chat a dead refresh loop is visible as a stopped
turn. In a tab it looks like a quiet rollout.

## 7. Smaller objections

- **Scope decided by effort.** "Included only if it is very easy" and "wanted if it is cheap" conflict with
  AGENTS.md ("effort never justifies the wrong architecture"). Each can be decided now:
  - *Moving a block into a group:* no. A block's id and kind are already fixed at show
    (`AgentBlockRules.cs:59,69-71`), a group stored in latest state survives Rewind (objection 1), and a
    journaled move is another row type. The cost to the user: "put these three in a dashboard" means the agent
    shows them again under new ids and the loose ones stay.
  - *The `/btw` reply box in the tab:* yes, and the fallback can go. The box takes only a session and a
    conversation id, and both mounts get the same composer owner, so the shared draft is free
    (`src/web/src/agent/composer-store.ts:33-34`). The work is the focus and selection catches in objection 5.
    A reply draft is not saved across reload (`composer-state.ts:384`), which the brief should accept or
    change.
  - *Adjustable chart height in the tab:* it is tab view state, which has a saved home
    (`EditorSessionModel.cs:21-22`). Decide on merit.
- **UI text.** The card header's "updated 14:32" repeats each block's own "updated"
  (`AgentBlockEntry.tsx:69-75`), and "4 charts · 1 table" counts what the expanded card shows. Both earn their
  place only on a collapsed card. A strip label restates its container; no label is an option.
- **Opening without focus** has a precedent, the `background` open intent
  (`src/web/src/editor/editor-controller.ts:511-513`), but `openAgentTab` always navigates (`:663-666`).
- **The agent's open** fits the block tools better than a command: they are bound to the calling conversation
  (`McpServer.BlockTools.cs:94,104`), so "open my dashboard" needs no id the agent cannot know.

## Alternatives

| | What it is | Gains | Loses |
| --- | --- | --- | --- |
| Brief | Name on the block's state; web gathers by name; one card tab; tray becomes the strip | One tool argument, no new row type | Everything in objections 1 to 4 |
| A. Journaled dashboard | Marker row with an id at first use; member rows stamped; collapse and dismissal as rows on that id | Placement from rows; restart-, `/clear`- and Rewind-correct by construction; stable tab path; typos visible to the model | A new row type and a container in the web model |
| B. Tab only, compact chat card | Full size lives only in the tab | No tall row, no dual mount | The agreed full-size chat card; data hidden until opened |
| C. Three tab kinds | Conversation tab (subagent, `/btw`), blocks tab (dashboard, lone block), task tab unchanged | Typed kinds, least code, no task regression | The "one tab" story |
| D. User pins blocks, no tool change | Grouping is a view feature | No protocol change | The agent cannot compose a dashboard, and pin state still needs a home |

**Recommended: A with C**, plus the visible one-line row from objection 2. It keeps the brief's best idea, a
group the agent names that survives "also add error rate", and everything the user agreed in the mockups. B is
a product choice to put to the user, not a correction.

## What survives

- **The tab holds no copy.** Each block is one signal both mounts read
  (`src/web/src/agent/block-store.ts:26,88-92`), and chart instances are independent
  (`src/web/src/agent/AgentChart.tsx:35`).
- **An agent-named group over grouping by turn.** The reason given is right.
- **Same name in main and `/btw` is two dashboards** by the existing conversation scoping
  (`AgentPaneConversationRows.ts:14-16`), and `/btw`s can show blocks; only subagents cannot
  (`src/Weavie.AgentClientProtocol/AcpConversation.cs:78`).
- **A partial Rewind keeps the rest**, provided the rows stay in the journal.
- **The shared `/btw` draft.**
- **Tab persistence and the "no longer exists" notice** exist for subagent tabs
  (`session-types.ts:13`, `AgentSubagentTab.tsx:26-28`).
- **The real-agent constraint.** Whether Claude and Codex pass a new optional argument reliably is exactly
  what it should catch.

## Recommended changes to the approach

1. Make the dashboard a journaled card with its own id; stamp member rows; drop "saved with the block's latest
   state" as the grouping fact.
2. Keep a one-line row where a later block was added.
3. Build the strip as running work plus cards, with dismissal journaled on the card id; decide the `/btw`
   listing rule; drop "most recent" defaults; give ✕ a command.
4. Replace "one card tab" with a conversation tab, a blocks tab, and the task tab as it is.
5. Decide the three effort-conditional items now, and state the dual-mount rules for table view state,
   selection target, focus, and collapse.
6. State that the tab is visible only in its own session, and put the full-size chat card to the user against
   the compact one.
