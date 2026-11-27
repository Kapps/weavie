# Challenge: code walkthroughs (second pass)

Argues against the approach in the revised [brief.md](brief.md). The first section says which objections from the
first pass the revision answers. The rest challenges what is new or still open, strongest first, each resting on
the code cited. The `weavie-architect` agent was consulted once, on which object owns a walkthrough's Asks and its
writes (objection 1); its findings are merged in, including a correction to my own framing (a `/btw` already forks
mid-turn).

**Verdict.** The revision fixes the two reuse claims that did not hold: evidence now keeps its own copy of the code,
and the Ask is owned instead of being a `/btw`. The product shape stands. What is left is one design gap and three
rules that produce wrong output:

- A walkthrough now has two writers (the working agent and each Ask) but is still a stored record that only its
  own conversation's tool row can write. Nothing owns the pending Ask, orders the two writers, or tells the working
  agent what the Asks added.
- The Ask is "told not to edit" and has "one tool", and neither is true of a fork today.
- A code block diffs "recorded copy → now", so its red and green lines keep moving after the walkthrough was
  written, and an Ask answered mid-edit shows half a change.
- Refusing a publish that cites an edited file contradicts "ask for a walkthrough at any time" and is not checkable
  with what the tracker records.

## What the revised brief answers

| First-pass objection | Status | Where |
| --- | --- | --- |
| 1. The review's before is not the walkthrough's before | **Answered.** Each piece of evidence keeps its own content-hashed copy; nothing reads the review's state. | Decision 6 |
| 1b. Marks on lines the edit changed have nowhere to sit | Open | Objection 5 below |
| 2. An Ask on `/btw` cannot write the walkthrough | **Answered in intent.** The walkthrough owns the Ask, which answers through one tool; Weavie writes the idea. The owning object and the write path are not designed. | Decision 7; objection 1 below |
| 2b. The Ask is neither hidden nor read-only | Open. "Told not to edit files" is guidance. | Objection 2 below |
| 2c. A mid-turn fork has undefined history | Acknowledged, moved to "To verify". The brief's reason is wrong (a `/btw` already forks mid-turn), and there is no design for a failed probe. | Objection 2 below |
| 3. The review's editor brings Keep and Revert | **Answered.** Read-only editors, no Keep or Revert. | "What the user sees" |
| 4. A walkthrough is not shaped like a block | **Mostly answered.** A new kind with its own limits; copies live beside it. Whole-block saves with two writers are open. Rewind keeping the latest state is now a stated decision. | Decisions 3, 6; objection 1 below |
| 5. "Checks every piece of evidence" overpromises | **Answered.** The model is data Weavie checks; the agent's unchecked values read as the agent's. | Decisions 4, 5 |
| 6. Trigger and update target rest on guidance | **Answered**, with one new problem: the setting's reach is stated, the agent names the walkthrough, and a late publish is refused. The refusal is objection 4. | Decisions 2, 4, 8 |
| 7. Five evidence shapes are one primitive | Not stated either way | Objection 6 below |
| 8. Background work, deleted files | **Answered in intent** (the roster does not list this kind of work yet, objection 2). Deleted files are decided. | Decision 7; Edge cases |
| 8. Ask cost, unsaved buffers | Open | Objection 6 below |

## 1. Two writers, one stored record, no owner

What the user would see: the agent's post-edit update lands and the two follow-up ideas the user asked for are gone,
or still state the pre-edit claim. Or the agent's update is dropped with a notice because an Ask answered a moment
earlier. Or "Tracing…" stays forever after the worker restarts.

"The walkthrough owns each Ask, the way a workflow run owns a stage it forks" borrows a pattern whose owner is a
live object. A workflow run's owner lives in the session, opens the fork, binds its grant and watches it end
(`src/Weavie.Hosting/HostSession.WorkflowConversation.cs:171-189`, `:237-245`), and the run's state is the workflow
engine's. A walkthrough is a row in `blocks`: one JSON value keyed `(provider, conversation, id)`, replaced whole on
each change (`src/Weavie.Core/Sessions/AcpSessionStore.Blocks.cs:10-19`). It cannot await anything.

- **The working agent never learns what the Asks added.** An Ask's idea is written by Weavie into the block. The
  working agent's context holds only what it published. There is no tool that reads a walkthrough back, and the
  blocks design accepts that an agent "does not remember" block state it did not send
  (`docs/specs/acp-rich-content/design.md:636`). After its edits the agent updates "the walkthrough it published"
  (Decision 8). It cannot update ideas 1.1 and 1.2, because it does not know they exist. The goal is that "the same
  walkthrough shows which ideas the change alters". That fails for exactly the ideas the reader asked about.
- **A block has one write path, and an Ask's answer is not on it.** A block changes only when its own conversation
  sees the tool's token on its own tool row and commits (`src/Weavie.AgentClientProtocol/AcpConversation.Blocks.cs:12-46`,
  `src/Weavie.AgentClientProtocol/AcpConversationPort.cs:27-33`). "Weavie writes the idea into the walkthrough
  itself" is a write with no tool row in the primary conversation, so the commit needs a second caller that the
  brief does not name.
- **Updates are checked early and applied late.** A block tool validates against the block as it stood at the call
  (`src/Weavie.Core/Agents/AgentBlockRules.cs:54`) and applies when the row is claimed (`:83-93`). The existing
  answer to overlap is guidance: "one update per id at a time" (`docs/specs/acp-rich-content/design.md:634`). An Ask
  cannot follow guidance given to another conversation. If the agent's update replaces the walkthrough, an idea an
  Ask wrote between the call and the claim is lost. If it is refused instead, the agent's turn sees "changed … before
  this update landed, so it was not applied" (`AcpConversation.Blocks.cs:43-45`) for something it could not have
  known.
- **Nothing holds "Tracing…".** The brief promises that an Ask that dies with the agent or the session becomes a
  visible failure. A workflow fork's state "survives an agent restart and a reload but not a crash, `/clear`, or
  Rewind" (`docs/specs/conversation-layer.md:176`). If the pending Ask lives only in memory, a crash leaves a
  placeholder nobody resolves. If it lives in the block, something must mark it failed at load, and that pass can be
  forgotten.

**The architect's answer: the precedent is `/btw`, not the workflow fork, and the owner is the agent session.**

- **Owner.** `AcpAgentSession` owns the Ask, as it owns `/btw` sides, through a new conversation role with its own
  port. A workflow's owner in `HostSession` cannot do it: only a port writes blocks (`AcpConversationPort.cs:27-33`),
  and the host session sees an owned conversation read-only and learns of its end from an observer
  (`HostSession.WorkflowConversation.cs:237-245`). The Ask's port is built holding the primary conversation it was
  asked from, the walkthrough and the idea, and the Ask's grant binds an answer sink to that port the way block
  claims are bound today (`src/Weavie.AgentClientProtocol/AcpConversation.Blocks.cs:8-10`). The answer tool takes no
  walkthrough or idea argument. An answer that arrives after the primary was replaced reaches a detached port and
  changes nothing (`AcpConversationPort.cs:28-29`, `src/Weavie.AgentClientProtocol/AcpConversation.Lifecycle.cs:136-145`).
- **Writes are operations on ideas, never replacements.** Every write applies to the block as it stands, under the
  gate the port already takes (`AcpConversationPort.cs:28-29`), so the whole-block save stores an ordered result. A
  replacement, which is what a chart or table update is today (`AgentBlockRules.cs:89-90`), would erase an answer
  that landed between the agent's call and its claim. Apply has to re-check that the idea still exists.
- **A host write fits the existing path.** A block write can carry no rows (`src/Weavie.Core/Agents/ConversationOutput.cs:42-49`),
  and the page picks a block up by revision (`ConversationOutput.cs:96`). What is missing is a second caller of the
  commit (`AcpConversation.Blocks.cs:38`), not a new row type.
- **Pending is the Ask's own marker row, not a field in the block.** The card derives its state: an answer present
  means answered, a marker that ended without one means failed, otherwise "Tracing…". Then no pass can be forgotten.
  Load already closes any started side marker that has no kept continuation
  (`src/Weavie.AgentClientProtocol/AcpAgentSession.Journal.cs:46-50`), which covers host restart and unload if the
  Ask keeps no continuation. Every other ending passes through one place (`AcpConversation.Lifecycle.cs:136-145`),
  where the Ask's port publishes its terminal row. `/clear` resets the journal and the blocks together
  (`ConversationOutput.cs:127-131`).

Still open after that answer: the working agent does not see Ask-added ideas (first bullet above), and the marker
row is published into the primary transcript (`AcpAgentSession.Conversations.cs:120-122`), so "never shown as chat"
needs the page to route it to the walkthrough card.

## 2. The Ask is still not read-only, not limited to one tool, and not hidden

What the user would see: a file changes that the working agent did not change, and the review attributes it to the
agent. Or "Tracing…" never ends because a permission request is waiting where nothing shows it. Or Rewind answers
"Wait for the agent to finish before rewinding" while the agent is visibly idle.

- **"Told not to edit files" is one sentence against the whole task.** A fork copies the working conversation,
  including the task it is in the middle of and Weavie's guidance (`src/Weavie.AgentClientProtocol/AcpAgentSession.Conversations.cs:108`).
  It runs in main's permission mode, and nothing can choose another: "The permission mode is copied from main"
  (`docs/specs/conversation-layer.md:92`; `HostSession.WorkflowConversation.cs:199`). Under auto-accept it can edit
  the worktree the working agent is editing. The tracker records a side conversation's edits as agent changes like
  any other (`src/Weavie.Core/Changes/SessionChangeTracker.cs:102`, `:146`). Under a prompting mode its request
  needs a card, and a hidden conversation has none.
- **"Its one tool" is not a restriction a grant can make.** A grant is a credential on the session's whole registry.
  It scopes the stage call, the session peers and the block claims, and holds no tool list
  (`src/Weavie.Core/Mcp/McpServer.Grants.cs:11-24`, `src/Weavie.Core/Mcp/CapabilityRegistryGrant.cs:16-32`). The Ask
  can still call `runCommand`, `messageSession`, `setSetting`, and the publish tool, which would create a walkthrough
  inside a conversation nobody can see.
- **"Hidden" has no role to use.** Every owned child publishes a "forking" row into the transcript
  (`AcpAgentSession.Conversations.cs:120-122`).
- **"Runs as background work" is new work, and today the opposite happens.** The roster lists the subagents and
  tasks an agent announces (`src/Weavie.AgentClientProtocol/AcpBackgroundWork.cs:15-17`,
  `src/Weavie.AgentClientProtocol/AcpRuntime.cs:169-171`), and the stop confirm counts only those
  (`src/Weavie.Hosting/HostCore.AgentBackground.cs:27-29`). A conversation's own turn is neither. So unload, Restart
  Agent and `/clear` would end a running Ask with no confirm, and Rewind would refuse flatly, with a message about
  the agent and no way to consent (`src/Weavie.AgentClientProtocol/AcpAgentSession.Rewind.cs:20`). Getting the
  brief's sentence needs the Ask listed as a background item, a Stop that handles it (Stop reaches tasks only,
  `src/Weavie.AgentClientProtocol/AcpAgentSession.Background.cs:16-22`), and a decision on two side effects of
  listing it: running work makes `/clear` and Rewind restart the agent's process
  (`src/Weavie.AgentClientProtocol/AcpAgentSession.cs:106-111`), and holds the session at Waiting, which holds the
  update drain (`docs/specs/background-work.md:76`).
- **The workflow role is the heavier one.** An owned workflow conversation needs the agent's session-close
  capability, or opening it throws (`AcpAgentSession.Conversations.cs:85`). A `/btw` needs fork support only
  (`AcpAgentSession.SideConversations.cs:76-78`). "The way a workflow run owns a stage" picks the role that fewer
  agents can host.
- **"Stalls" has no definition.** The only general test for a stall is elapsed time, which is the safety-net timeout
  AGENTS.md rules out. The states Weavie can observe are a pending permission or question and a running turn
  (`src/Weavie.AgentClientProtocol/AcpConversation.cs:156-161`). The brief should name those: an Ask that needs
  input is shown as needing input, and a running Ask is running until Stop.
- **The mid-turn fork carries the feature and has no plan B.** The brief lists it under "To verify" because "every
  existing fork waits for the turn to end". That is true of workflow forks (`HostSession.WorkflowConversation.cs:170`,
  `:177`) and false of `/btw`, which forks the moment it is asked, turn running or not
  (`AcpAgentSession.SideConversations.cs:23-29`). So Weavie already ships a mid-turn fork, and the first pass was
  wrong to call it untested in practice. What nobody has checked is what Claude and Codex copy when the fork lands
  inside a tool call. If that history is broken or partial, Asks can only run between turns, which removes "never
  interrupts the working agent" in the case the Goal calls most useful. See [Alternatives](#alternatives-for-the-ask).

## 3. "Recorded copy → now" has a before and no after

What the user would see: a walkthrough written an hour ago whose red and green lines include the next task's edits.
Or the claim says "the guard now returns early" beside a block with no diff, because the user reverted that hunk in
the review. Or two blocks on the same file in one walkthrough show different amounts of change.

- **The claim is fixed when the agent updates; the diff is not.** "Each block shows the diff from the code as it was
  when its evidence was recorded to the file now." After the update, "now" keeps moving: later turns, the user's
  typing, a Revert in the review. The walkthrough is saved with the conversation and is read later, so this is the
  normal case. An update should record the file's hash as the after. A block whose file no longer matches says
  "changed since this walkthrough was updated", which is a hash comparison against a text already stored.
- **Per-evidence copies meet mid-edit Asks.** I recommended per-evidence capture in the first pass, and with Asks
  that run while the agent edits it has a consequence: "Evidence added after the edits records the edited text"
  (Decision 6) also covers evidence added *during* the edits. An Ask answered when the agent is halfway through a
  file records the half-edited text. Its block then shows only the second half of the change as "the change", next
  to an earlier block on the same file that shows all of it. The walkthrough needs one before per file: the first
  copy it recorded of that path. Each piece of evidence still places its marks against its own copy, and every
  block on that file diffs from the walkthrough's first copy.

## 4. Refusing a publish that cites an edited file

What the user would see: they ask for a walkthrough of the change in progress and the agent says it cannot publish
one. Or, in a long conversation, a file touched by the first task can never be cited in a walkthrough again.

- **It contradicts Decision 2.** "The user can also ask for a walkthrough at any time, with or without a change in
  progress." Mid-change, the file worth explaining is the one already edited.
- **It is asymmetric, so it is a rule to route around.** An update may cite edited files ("Evidence added after the
  edits records the edited text"). A publish may not. The agent that hits the refusal publishes a stub and updates
  it.
- **"The conversation has already edited" has no source.** The tracker is per session, not per conversation. It
  keeps each path's text before its most recent edit, never clears it, and ties a conversation only to its prompt
  (`src/Weavie.Core/Changes/SessionChangeTracker.cs:34-35`, `:102-113`, `:175`). A refusal built on it also fires
  for edits by a `/btw`, a workflow stage, or last week's task in the same session.
- **It misses the edits it exists to catch.** A shell command's file changes are not reported: the mutation is
  `None` for "a shell command or tool call whose file side-effects the provider doesn't enumerate"
  (`src/Weavie.Core/Agents/AgentEvent.cs:55-59`). `sed -i`, a formatter or a code generator, then publish: accepted,
  and shown as the code before the change.

**Better:** accept the publish and say what it is. Evidence on a file the session's agent has edited carries "recorded
after edits to this file began", from the same tracker fact. The user who asked mid-change gets their walkthrough,
and the before-walkthrough that came late is labelled instead of refused. The cost is that a late publish is
possible, which it already is through the shell.

## 5. Marks on changed lines still have nowhere to sit

What the user would see: after the edit, the numbered marks of a *compare* or *deciding line* sit on the wrong line,
on the lines that matter most.

Decision 6 "places its marks by mapping lines across the diff". The mapping is exact only for lines the edit left
alone. A line inside a changed range "anchors after that range's replacement" and reports `exact: false`
(`src/web/src/editor/line-anchor.ts:14-25`). A review hint can live with that, since it is a note beside a line
(`src/web/src/editor/review-hints/hint-anchor.ts:23-24`). A token mark cannot: its token is on a removed line, which
exists only as a ghost in a view zone (`src/web/src/editor/review/diff-zones.ts:1-3`). The lines an explanation
cites are the lines the change is most likely to touch.

The brief should pick one: draw marks inside removed-line zones (a second renderer), or have an update re-record the
evidence of every idea it alters, so before-marks are drawn on the recorded copy and after-marks on the file. The
second fits objection 3's after-hash and keeps one renderer, at the cost of the agent resending that evidence.

## 6. Smaller gaps

- **"Show more lines … as in the review" does not fit a block with no diff.** The review hides what is far from a
  change, and with no change it hides nothing (`src/web/src/editor/review/review-context.ts:22-30`). A walkthrough
  published before the edits has no diff, so each block would show the whole file. Cited spans have to drive what
  is shown, with changes added to them.
- **The read-only diff mode has never run.** `InlineDiff` declares `view` (`src/web/src/editor/inline-diff.ts:46`),
  and its only callers pass `review` or `applied` (`src/web/src/editor/editor-controller.ts:1161`, `:1176`, `:1209`).
  My first-pass recommendation leaned on it. Plan it as new code, under the render budgets AGENTS.md requires for
  editor work, with one editor per opened piece of evidence.
- **"Review hints already make these checks" overstates.** Hints check path, text and line range
  (`src/Weavie.Core/Review/Hints/ReviewHintStore.cs:127-146`). Tokens on a line, line ranges and the edited-file rule
  are new. Hints read disk (`:140-141`), so evidence on a file with unsaved edits is checked against text the user
  is not looking at. Still undecided.
- **Copies have two deletion paths.** Hints drop a source once no hint references it
  (`src/Weavie.Core/Review/Hints/ReviewHintStore.Persistence.cs:27-31`). A walkthrough's copies need the same count
  in the conversation store, on both ways a block goes: its row is retracted, or history is replaced
  (`src/Weavie.Core/Sessions/AcpSessionStore.Blocks.cs:23-28`, `:41-54`).
- **A file too large to diff.** The line diff refuses large files (`src/web/src/editor/line-anchor.ts:27-31`). The
  brief does not say what a block shows then.
- **Ask cost.** A fork takes its source's model and options (`AcpAgentSession.Conversations.cs:88-89`). Each
  question is a full-context turn on the working model. Undecided.
- **"A call can open in place"** does not say who supplies the target: the agent (then it is evidence, and checked)
  or the language server at render time (then it is unchecked and absent without a server).
- **Evidence shapes.** The brief still lists five and does not say whether they are one anchored-span type with a
  layout. The first pass argued for one.

## Alternatives for the Ask

| | How it runs | Gains | Costs |
| --- | --- | --- | --- |
| **A. Brief** | Fork of the working conversation, taken mid-turn, on the agent's process | Knows what the agent knows, answers while it works. `/btw` already forks this way. | What the fork copies mid-tool-call is unverified. Main's permission mode, full registry, shared worktree. Blocks Rewind. Full-context cost per question. |
| **B. Fork between turns** | As A, but taken once the working turn ends, as workflow forks are | Uses fork behaviour that is tested today | An Ask during a long edit waits for it. Everything else as A. |
| **C. Fresh restricted conversation** | A transient process in the worktree, given the walkthrough, the idea and its recorded copies; permission requests rejected, as consult does (`docs/specs/agent-consultation.md:62-70`) | No dependency on forks or session close. Never touches the working agent's process, so Rewind and `/clear` are unaffected. Model can be cheaper. Edits are rejected whenever the provider asks. | Starts without the session's context and must re-read code. Process start per question (or one kept per walkthrough). Consult passes no MCP servers and takes one turn, so the answer tool and the retry after a refusal are new. A provider set to act without asking can still edit. |
| **D. Ask the working agent** | A queued prompt to the working conversation | No new conversation. The working agent knows every idea, which removes objection 1's first point. | Interrupts or waits for the working agent, which the brief rules out. |

C is the one worth a prototype beside A's fork probe. A walkthrough is already a compact statement of the theory
with anchors into the code, which is most of the context an Ask needs, and C makes "does not edit" and "does not
disturb the working agent" true by construction instead of by instruction. If the mid-turn probe fails, C or B is
the design, and the brief should say which.

## Recommendation

1. Replace "the way a workflow run owns a stage" with the architect's shape: the agent session owns the Ask through
   a `/btw`-like role whose port holds the walkthrough and idea, pending state is the Ask's marker row, and
   walkthrough changes are operations on ideas (add, change claim, replace evidence) applied in arrival order. Give
   the working agent the current walkthrough when it updates, so follow-up ideas are updated too.
2. Decide the Ask's limits as mechanisms: a grant that admits only the answer tool, a marker the page routes to the
   card instead of the transcript, a roster entry with Stop and consent on Rewind, whether a running Ask holds the
   session at Waiting, and what a pending permission looks like. Define "stall" by observable state.
3. Run the mid-turn fork probe on real Claude and Codex first, and write down the design for a failed probe (B or C).
4. Record an after-hash on update and one before per file per walkthrough.
5. Replace the edited-file refusal with a label.
6. Decide where marks on changed lines are drawn.

## Not verified

- What Claude and Codex copy on `session/fork` when it lands inside a tool call. `/btw` takes this path today, so
  a probe is a matter of asking one during a long edit on each agent.
- How the page treats a side marker row it should not draw in the transcript; the architect did not read the web
  side, and neither did I.
- Whether a transient process can be given an MCP tool and a second turn without the rest of the registry
  (alternative C).
- Whether a grant can be narrowed to one tool without the agent's built-in tools also needing to be restricted
  through the provider.
- The cost of mounting one read-only diff editor per opened piece of evidence in WebKit.
- Whether Rewind restores file contents; if it does, a kept walkthrough's "after" claims describe edits that are no
  longer on disk.
