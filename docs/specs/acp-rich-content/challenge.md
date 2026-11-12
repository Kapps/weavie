# Rich content in the agent pane — challenge

**Status:** challenge to [brief.md](brief.md). Evidence is the code in this worktree plus a static read of the
installed real adapters (`claude-agent-acp` 0.102.0, `codex-acp` 2.2.2). Nothing was run against a live agent;
each unverified link is marked.

**Verdict.** Weavie-drawn blocks from a tool with ids is the right product shape. The approach around it is weak
in four places: the motivating case is not met, the Phase 1 link is built on the wrong join, silent folding
contradicts the brief's own edge case, and the block has no owner. A materially better approach exists for each;
none of them changes what the user sees.

## 1. The model is the data pipe, so the motivating case is not met

*What the user gets:* a rollout chart that costs model tokens every minute, fills the agent's context until it
compacts, and may not refresh at all on Codex.

The brief's goal is a chart "refreshed every minute … without the session growing". Phase 1 only stops *pane
rows* growing. Three other things still grow, and Weavie can fold none of them:

- **The agent's context.** "The agent supplies data" through a tool call means each refresh reads the fetch
  result in and writes the whole dataset out as tool arguments. Both adapters carry those arguments in the
  stream as the call's input (Claude: `rawInput = chunk.input`, adapter `dist/acp-agent.js:11069-11076`; Codex:
  `input: { server, tool, arguments }`, adapter `dist/index.js` `McpToolReporter`). A day of minute refreshes is
  1,440 re-emissions of the series, each one a chance to transcribe a number wrong.
- **The saved history.** Every emitted event is committed (`src/Weavie.AgentClientProtocol/AcpConversationPort.cs:127-136`
  → `INSERT INTO pane_events`, `src/Weavie.Core/Sessions/AcpSessionStore.cs:122`), and a tool row stores its full
  `rawOutput` (`src/Weavie.AgentClientProtocol/AcpConversation.Tools.cs:109-114`). The fetch rows that feed a
  refresh are journalled whether or not the pane shows them.
- **The tray.** Finished background items stay until the next user prompt
  (`src/Weavie.AgentClientProtocol/AcpConversation.PromptDelivery.cs:40`, `docs/specs/background-work.md:57`).

"Refreshes on its own schedule" also rests on nothing Weavie owns. The ACP path has no scheduler or wake-up
(no match for wakeup/cron/timer under `src/Weavie.AgentClientProtocol`); a scheduled wake-up is known only to
terminal Claude's Stop hook (`src/Weavie.Core/Hooks/HookRequest.cs:49-54`). In ACP a self-started turn exists
only as an AIR task of type `turn` (`src/Weavie.AgentClientProtocol/AcpBackgroundWork.Tasks.cs:23`). Whether
Codex can wake itself is unverified; without it a refresh loop is one endless turn of `sleep`, which holds the
turn against the user. The turn number advances only on a prompt (`AcpConversation.Turns.cs:68`), so every
refresh item is filed under the last prompted turn.

"The call … costs no extra turn" is true for the first show and false for every refresh.

**Better: let a block take its data by reference.** The tool accepts `data` inline *or* as a workspace file. The
agent writes a fetch script, runs it as a background command, and shows the block once; Weavie redraws when the
file changes, using the session's own watcher facts (`FilesInvalidated`, `docs/concepts/session-file-activity.md`).
A refresh then costs no model tokens, no tool call, no transcript row, and no wake-up; the script exits non-zero
on an anomaly and the agent speaks. Liveness and Stop come from the existing background tray.

| | Inline only (brief) | Inline + by reference |
| --- | --- | --- |
| Tokens per refresh | Dataset in and out | None |
| Rows to fold | Fetch + update per refresh | None |
| Needs agent wake-up | Yes, provider-specific | No |
| MCP-only sources (Datadog MCP) | Works | Still inline; the brief's reason holds here |
| Cost | — | Watching one named file per block; a script must reach the source |

The brief rules out "Weavie-side polling" because Weavie cannot reach the agent's MCP servers. That is true —
Weavie advertises only its own server to the agent (`AcpConversation.Opening.cs:176-194`) — but it is a reason
for inline data to *exist*, not for it to be the only path. Reading a file the agent names is not polling a
source. "Charts and tables come from a Weavie tool, and only from it" stays true: the tool still creates the
block; only the data's route changes.

## 2. Phase 1 step 1 joins the two streams from the wrong side

*What the user gets if it goes wrong:* a chart in the wrong place, twice, or a tool call that hangs.

The brief says "today nothing connects the two". Half of that is already built: a Weavie tool call knows its
conversation. Each conversation incarnation has its own credential (`src/Weavie.Core/Agents/AgentConversationBindings.cs:13-26`),
each call is an `InflightCall` bound to that grant (`src/Weavie.Core/Mcp/McpServer.Cancellation.cs:13-21,44-49`),
and the grant dies with the incarnation (`AcpConversation.Lifecycle.cs:182-189`). Only call → *row* is missing.

Building that from the tool server's side needs an identifier both streams share, and the evidence says there
is no provider-neutral one:

- **Claude:** the CLI binary adds `_meta["claudecode/toolUseId"]` to MCP requests, and the adapter uses the same
  tool-use id as `toolCallId` (`dist/acp-agent.js:11076,11153`). Read from the binary, not seen on the wire, and
  undocumented.
- **Codex:** no call id found. The adapter's own source says the MCP-level id "never reaches the client" and
  works around it by queueing call ids per `(thread, server)` (`dist/index.js:32850-32857`, `pendingMcpApprovals`).
  That is the heuristic the brief would have to repeat.
- **Weavie today** reads only `progressToken` from `_meta` (`src/Weavie.Core/Mcp/McpServer.Http.cs:63-69`), and
  the fake agent sends a bare `tools/call` with no `_meta` and no `tool_call` narration
  (`tools/Weavie.FakeAcp/FakeAcpAgent.WeavieTools.cs:19-38`), so no current test can exercise the link.

Without a shared id the join is name + arguments + timing, across two channels with no ordering between them:
ACP updates serialize under the conversation's transition gate (`AcpConversation.Updates.cs:8-12`), MCP requests
are dispatched off a separate receive loop (`src/Weavie.Core/Mcp/McpServer.cs:247`). A handler that waits for
its narration hangs when narration is late or absent; one that does not wait cannot place the block. Two
parallel calls with equal arguments are indistinguishable — the brief's own edge case. A per-provider id is the
"provider-specific host branch" `docs/concepts/native-acp-agents.md:18-20` rules out.

The same weakness makes "out of scope: blocks from native subagents" unenforceable. A subagent uses its
parent's binding (`AcpConversation.Background.cs:20`, `AcpConversation.cs:78`), so its call is admitted as the
parent's while its narration arrives on the subagent's conversation
(`AcpConversation.Tools.cs:14`: "announced only there"). The tool cannot tell, so it cannot refuse.

**Better: the tool's result carries the link.** The handler validates, stores the block, and returns a short
single-use token in its result. Both adapters relay a tool's result on the row that made the call (Claude:
`rawOutput: chunk.content` at `dist/acp-agent.js:11156`; Codex: `opaqueResult: { result, error }`), and Weavie
already reads both fields there (`AcpConversation.Tools.cs:105-114`). The owning `AcpConversation` sees the
token on its own `tool_call_update` and turns that row into the block.

| | Tool server finds the row (brief) | Row claims the block (token in result) |
| --- | --- | --- |
| Shared id needed | Yes; Claude-only, undocumented | No; Weavie wrote the token |
| Ordering between channels | Must be invented | None needed; the row exists before its result |
| Parallel equal calls | Ambiguous | Each result has its own token |
| Subagent calls | Misplaced or never placed | Land where narrated; refusable there |
| Who writes the transcript | Tool server, outside the port | The conversation, through its port |
| Risk | Hang or wrong row | An adapter that drops tool results shows a plain tool row |

The risk in the last row is unverified for agents other than these two, and must be proven on the wire for
both before design, as the brief already requires. An unclaimed block fails visibly: the agent's tool row
still shows, with no chart.

"One object per Weavie tool call" for all ~30 tools is more than the feature needs; only block tools need a row.

## 3. Silent folding cannot be decided when rows are emitted, and undoing them grows history

*What the user gets:* rows that appear and then vanish, or successful shell commands that are never shown.

A tool row is emitted and journalled when its `tool_call` arrives (`AcpConversation.ToolContent.cs:10-39`). The
fetch comes *before* the update call, so "activity that only feeds a block update" is knowable only afterwards,
and is wrong whenever the agent then speaks from the same fetch. The only undo the code has is retraction,
which appends another event (`AcpConversation.Turns.cs:201-216`); the ledger replaces a row in place and never
removes one (`src/Weavie.Core/Agents/AgentPaneLedger.cs:111-116,136-141`), and the journal is insert-only
(`AcpSessionStore.cs:122`, read back whole at `:24`). So a folded refresh costs more saved events than an
unfolded one. That contradicts the brief's edge case "must not grow saved history or reconnect replay per
update". Holding rows back until the turn's fate is known would delay every tool row in every turn.

The pane also already folds: consecutive activity collapses into one "Working" row
(`src/web/src/agent/AgentActivitySegments.ts:22-57`, `src/web/src/agent/AgentPaneMessages.ts:107-114`), broken
only by a durable entry (`AgentPaneMessages.ts:98`).

**Better: drop step 3.** Suppress only Weavie's own update row, at the source, the way a released plan or a
subagent spawn already is (`AcpConversation.ToolContent.cs:10-15`); with objection 2's token the conversation
knows that row is an update before it completes. Leave third-party rows to the existing grouping. With
by-reference data (objection 1) the common refresh produces no rows at all. Trade-off: an inline refresh from an
MCP source still shows one collapsed "Working" group per refresh. That is honest — the work happened and cost
tokens.

## 4. The block has no owner, and "latest state" has no home

*What the user gets:* after Rewind, a chart showing data from a future that was discarded; after a restart,
"id does not exist".

The brief gives the content to the tool server and the row to "the handler". The code gives both to the
conversation: the port is its "only event sink and its only way to persist, publish", gated and inert once
detached (`docs/concepts/acp-conversations.md`, "Four owned channels"; `AcpConversationPort.cs:23-25`). A tool
server that writes rows is a second writer outside that gate.

"Stored and replayed as its current state" also collides with how history works:

- **The journal is an event log.** One row per event, no update statement (`AcpSessionStore.cs:122,163`). Plans
  are the existing id-addressed, updated-in-place item and they still append a full copy per update
  (`AcpConversation.SessionUpdates.cs:46-70`). Blocks done the same way append the whole dataset each refresh.
- **Rewind keeps a prefix of that log** (`src/Weavie.AgentClientProtocol/AcpRewindPlan.cs:27-33`, committed at
  `AcpAgentSession.Rewind.cs:66-68`). A prefix of events is a valid past; a latest-state block is not. The brief
  does not say what Rewind, `/clear`, or restart do to a block.
- **A row's identity includes the provider session and turn** (`src/Weavie.Core/Agents/AgentPaneIdentity.cs:10-13`).
  An update after the session id changes would add a second row instead of replacing the first. Inferred from
  the key, not run.
- **Other readers see nothing.** `readSession` projects prompts, replies, tool summaries and errors only
  (`src/Weavie.Core/Agents/AgentTranscript.cs:37-46`).

**Better: the block is its own session-owned thing.** One block store per conversation slot, keyed by block id,
upserted like the `conversations` table already is (`AcpSessionStore.cs:46`). The transcript journals one row
per block, once, holding only the id and the facts layout needs. Updates write the store and publish one small
bus message; the journal, the ledger revision, and transcript projection are untouched. Rewind and `/clear`
drop blocks with the rows that referenced them, in the same transaction that replaces the journal
(`AcpSessionStore.cs:82-109`).

| | Latest state inside the pane journal (brief) | Block store + reference row |
| --- | --- | --- |
| Saved growth per update | One full event, or a new upsert path inside an event log | One overwritten record |
| Reconnect | Row re-sent per revision | Row unchanged; block fetched by id |
| Rewind | Undefined | Follows the row; a kept block keeps its latest data, stated |
| Cost | Second storage model hidden in the journal | A second table, plus cleanup on Replace and delete |

Still to decide either way: a block shown before the rewind point and updated after it keeps post-rewind data.
Say so in the spec, or freeze a block when its conversation is replaced.

## 5. "Remote addresses are not restricted" reasons from the wrong machine

Agent replies block images today, on purpose (`src/web/src/agent/AgentMarkdown.tsx:17-22`,
`src/web/src/editor/preview/markdown-renderer.ts:45-48`). The brief's reason for lifting that — "the agent
already has full access" — is about the agent's host. The image is fetched by the *user's browser*, which on
the remote transport is another machine on another network.

- **It bypasses the permission gate.** Rendering is not a tool call. Text an agent copies from a log line or a
  web page becomes a GET with data in the URL, with no approval, in every permission mode. The motivating case
  displays log messages.
- **Crop cannot work.** "An image region is sent as the cropped picture" needs a canvas read
  (`src/web/src/agent/pasted-images.ts:57-80` is the existing path); a cross-origin image taints the canvas.
- **WebKit hangs** decoding a video handed to an `<img>` (`src/web/src/agent/github-media.ts:24`), which is why
  GitHub media asks the host what each attachment is first.

**Better:** the host fetches and serves the image same-origin behind the existing passive-media gate
(`docs/specs/workspace-http-media.md`, "Media scope"). That fixes crop and the WebKit hang, and makes the fetch
come from where the agent runs. Both behaviours are defensible, so the policy is a setting. Cost: a host fetch
path with a size bound that must fail visibly in the pane.

## 6. Smaller objections

- **"Data loads when it is on screen" is the pattern the transcript handles worst.** Row estimates are text
  only (`src/web/src/agent/AgentPaneEstimate.ts:13-30`), the virtualizer never corrects position on a size
  change (`src/web/src/agent/AgentTranscriptViewport.tsx:132-133`), overscan is 4 (`:129`), and only pending
  forms stay mounted (`src/web/src/agent/AgentPaneLayout.ts:82-90`). The existing deferral loads on *expand*,
  not on visibility (`src/Weavie.Hosting/Agents/AgentPaneProtocol.cs:44-48`). A block whose height is unknown
  until its data arrives shifts the transcript; a block being brushed unmounts on scroll. Give the reference
  row a deterministic height, fetch block data in one batch after history, and keep a block with an active
  selection mounted.
- **"Reuses the zoom Mermaid diagrams have."** In the agent pane Mermaid has a Source/Preview toggle and no zoom
  (`src/web/src/agent/agent-mermaid.ts:27-75`); zoom exists only for the editor preview
  (`src/web/src/editor/preview/embed-zoom.ts:116-135`) and works by cloning DOM
  (`src/web/src/editor/preview/EmbedLightbox.tsx:166-177`), which leaves a canvas chart blank and a live chart
  frozen. Both commands pick "newest" or "first" (`agent-mermaid.ts:77-103`); block zoom and attach must be
  registered by the block that owns them.
- **"Like a pasted image" reuses little.** Attachments are image files end to end
  (`AcpConversation.PromptBuilder.cs:66-80,95-106`). A data attachment needs a new submission kind, journal row
  and chip. The carrier exists: the editor selection already rides as an embedded resource (`:42-50`), gated on
  `EmbeddedContext`; say what an agent without it receives.
- **A silent monitor that dies looks like a quiet rollout.** "Shows its last update time and nothing else" makes
  an ageing timestamp the only sign the agent stopped. Let the tool state when the next update is due and mark
  the block overdue; that claims lateness, not liveness.
- **"Follows the theme by construction"** holds only for DOM/SVG drawn with theme variables. Mermaid already
  needs an explicit re-render on theme change (`AgentMarkdown.tsx:64-66`); a canvas library would too. Make it a
  selection criterion for the charting library.

## What survives

Weavie-drawn blocks, a tool with ids and validation errors, markdown tables left alone, blocks in place in the
transcript, agent-driven refresh for MCP-only sources, and proving the link on real Claude and Codex first.

## Recommended changes to the approach

1. Blocks accept data by reference to a workspace file as well as inline; file change redraws the block.
2. Phase 1 step 1 becomes: a block tool's result carries a token; the conversation claims its own row. Capture
   the real wire for Claude and Codex before designing further.
3. Phase 1 step 2 becomes: a session-owned block store, one reference row per block, defined behaviour on
   Rewind, `/clear`, and restart.
4. Phase 1 step 3 is deleted; only Weavie's update row is suppressed at the source.
5. Remote images are fetched by the host, behind a setting.
