# Workflow context reuse: challenge

An independent challenge of [brief.md](brief.md), from the brief and the repository alone, with the repo's architect
agent. Every code claim was read at the cited lines; nothing was run.

The goal is sound. The approach has four problems that change the design: its mechanism is not the existing one it
claims to extend, it changes what `fresh` means for every workflow, its saving is assumed while the means to see it are
excluded, and the conversation that now outlives a visit has no owner. [Alternatives](#alternatives) gives a narrower
design.

## Objections

### 1. Close-then-reopen in one process is not an existing mechanism, and today's code rejects it

The brief: "BTW asides already save and reopen a provider session, so this extends an existing mechanism", and
"close-and-reopen is one path that every run exercises".

An aside is kept open, not closed and reopened:

- A reply goes to the live conversation (`src/Weavie.AgentClientProtocol/AcpAgentSession.SideConversations.cs:32-36`).
  The aside ends only on a terminal failure (`src/Weavie.AgentClientProtocol/AcpNestedPorts.cs:56-58`).
- The test asserts it: two prompts reach one side session and the only load is the fork's own
  (`tests/Weavie.Hosting.Tests/AcpAgentSessionTests.cs:158-176`).
- The saved continuation is used only when no live conversation exists
  (`src/Weavie.AgentClientProtocol/AcpAgentSession.SideConversations.cs:37-42`): after a restart, a rewind, or a cold
  start. Those are reopens in a new process generation.

Reopening a closed session in the same process fails as the code stands:

- An endpoint keeps its session id after it retires (`src/Weavie.AgentClientProtocol/AcpSessionEndpoint.cs:14,19-21`).
- Endpoints leave the connection's table only when the process stops
  (`src/Weavie.AgentClientProtocol/AcpJsonRpcConnection.Endpoints.cs:17-25`, called from
  `src/Weavie.AgentClientProtocol/AcpJsonRpcConnection.cs:206`).
- Binding a session id another endpoint of that process holds throws "already has an owner"
  (`src/Weavie.AgentClientProtocol/AcpJsonRpcConnection.Endpoints.cs:95-98`), and a restore binds before it sends
  (`src/Weavie.AgentClientProtocol/AcpConversation.Opening.cs:37`).
- For a stage child that exception fails the whole runtime
  (`src/Weavie.AgentClientProtocol/AcpAgentSession.Conversations.cs:93-94`): main and every sibling go with it.

Nothing orders the close before the reopen either. The old visit's owner closes its conversation in the background
after the visit retires (`src/Weavie.Hosting/HostSession.WorkflowStageOwner.cs:28-41`), while the next visit's owner is
created as soon as its turn is owed (`src/Weavie.Hosting/HostSession.Workflow.cs:53-59`).

So the path "every run exercises" is one no code takes today, and no test or real-agent probe in the repository shows
that an agent will load a session it closed in the same process. The mechanism needs endpoint succession per session
id, a serialized close-then-open per conversation, and that probe, before it can be called an extension.

The capability is also misnamed. The existing restore prefers `session/resume` and uses `session/load` only when
resume is absent, accepting either (`src/Weavie.AgentClientProtocol/AcpConversation.Opening.cs:26,31-34`). Load replays
the whole transcript, which Weavie parses and drops (`src/Weavie.AgentClientProtocol/AcpConversation.Updates.cs:40-44`),
and opens serialize per connection (`docs/specs/conversation-layer.md:93-94`), so a large reload blocks other opens.
The requirement is "load or resume", and it is new for `fresh`, which today needs only `session/close`
(`src/Weavie.AgentClientProtocol/AcpAgentSession.Conversations.cs:33`).

### 2. Reuse becomes the meaning of `fresh`, and a new conversation per visit can no longer be written

`fresh` is documented as independence:

- `docs/specs/workflows.md:222-223`: "`fresh` is for deliberate independence (a reviewer that must not inherit the
  author's assumptions)"; `docs/specs/workflows.md:241` gives "an independent review".
- The schema: "fresh owns a new conversation" (`src/Weavie.Workflows/WorkflowSchema.cs:34`).
- The authoring prompt: "fresh for independent judgment" (`src/Weavie.Core/Mcp/AuthorWorkflowPrompt.cs:30-33`).

The brief keeps the word and changes what it does on every visit after the first, in every existing workflow, with no
edit to the file. Its escape is "a workflow that wants new eyes uses a separate stage". That covers a fixed number of
visits only: stage ids are static and a loop returns to the same id
(`src/Weavie.Workflows/WorkflowTransitions.cs:175-178`). Jump to Stage reuses too, so a stage conversation that has
gone wrong stays for the run short of `/clear`, which also clears main.

The workflow that ran this challenge shows it. Its `challenge` stage is `context: fresh` with the prompt "You have only
the brief and the repository", and `weigh`'s `rethink` choice routes back through `interview` to `challenge`
(`~/.weavie/workflows/feature-build.yaml`, a personal workflow outside the repository). Under the brief the second
challenge continues the first, holding the superseded brief and its own earlier verdict. The same file asks for "a
fresh-context review of the fix" in `ci` to avoid exactly that.

No built-in workflow uses `fork` or `fresh` (`src/Weavie.Workflows/BuiltIn/quick-fix.yaml`, `spec-driven.yaml`), so the
exposure is user workflows.

The opposite default loses nothing: `fresh` and `fork` open a new conversation on every visit, as today, and reuse is
asked for. Both behaviours stay expressible and no existing workflow changes meaning.

### 3. The saving is assumed, and the brief excludes the means to see it

The case is three clean passes at 260k, 206k, and 130k tokens. The brief does not say whether those are context sizes
or billed totals.

- **Window.** A returning visit starts with everything earlier visits read. Codex reported a 258k window on Astra,
  and a conversation over the window "is the agent's to compact; if it errors instead, the turn fails and the run
  pauses" (`docs/specs/workflow-agent-selection.md:188-190`). If 260k is a context size, the first return on that model
  starts at the limit, and compaction discards the context reuse exists to keep.
- **Cache.** A model switch re-reads the whole conversation uncached, measured at
  `docs/specs/workflow-agent-selection.md:185-187`. The brief allows that switch on a reused conversation and concedes
  the same re-read past the provider's cache lifetime. "A fix stage usually fits inside it" has no measurement in the
  repository, and a fix stage that writes tests, reruns them, and pushes is the slow stage of the loop.
- **Carry.** With a warm cache, every request in a later visit still carries the full earlier history; cached input is
  discounted, not free. A clean third pass cost 130k; a reused one carries about 466k through each request. Reuse wins
  only when the return visit makes far fewer requests than a clean pass. (Provider pricing is not in the repository;
  this is what the measurement must settle.)
- **Observability.** A child conversation's usage is dropped (`NestedPort.OnUsageChanged` is empty,
  `src/Weavie.AgentClientProtocol/AcpNestedPorts.cs:42`; the primary's is forwarded,
  `src/Weavie.AgentClientProtocol/AcpConversationPort.cs:165`), and the brief excludes token reporting. After this
  ships nobody can tell whether a reused stage is cheaper.

The brief puts the measurement last and asks only whether the reload reads from the cache. It decides whether the
feature has value, so it is a gate before the design: billed tokens for the PR #1194 loop, reused against clean and
against option D below, on real Claude and Codex, with a realistic fix-stage duration.

### 4. Nothing owns the conversation that outlives a visit

Today each lifetime has one owner. The runner owns "one exact stage visit" and Hosting's `WorkflowStageOwner` "owns the
child handle" for that visit (`docs/specs/conversation-layer.md:23-24`); "IDs remain at protocol, persistence, and UI
boundaries; the workflow owner retains an object" (`docs/specs/conversation-layer.md:84`). The owner is created per
visit (`src/Weavie.Hosting/HostSession.Workflow.cs:54-56`).

The brief adds a lifetime between run and visit, gives it to no object, and splits one fact across two stores:

- The run file records "the conversation it last opened" per stage. That is the standalone engine's record
  (`src/Weavie.Workflows/WorkflowRun.cs:50`, `src/Weavie.Core/WeaviePaths.cs:180`), which references only YamlDotNet
  (`src/Weavie.Workflows/Weavie.Workflows.csproj`) and holds no conversation identity today.
- The conversation layer saves the continuation in the ACP store
  (`src/Weavie.AgentClientProtocol/AcpNestedPorts.cs:51-54`).

Each changes without the other:

- `/clear` empties the ACP store (`src/Weavie.AgentClientProtocol/AcpAgentSession.Process.cs:70`) and has no path to
  the runner (`src/Weavie.Hosting/HostSession.cs:623-629`).
- Rewind drops continuations by anchor (`src/Weavie.AgentClientProtocol/AcpAgentSession.Rewind.cs:66-67`).
- A run ending changes only the run file (`src/Weavie.Workflows/WorkflowTransitions.cs:152-161`), so its resumable
  rows stay in the store with nothing to delete them.

The host then cannot tell "forgotten on purpose" from "failed to reopen", and the brief's "Nothing to reopen" row
becomes the thing that reconciles the two stores.

Saving a stage continuation "as a BTW aside does" has a second effect. Any entry in the aside map with a session id is
reopenable by a reply, as an aside with no stage authority
(`src/Weavie.AgentClientProtocol/AcpAgentSession.SideConversations.cs:37-42`;
`src/Weavie.Core/Agents/AgentConversationBindings.cs:20-26`). A left stage's card is read-only on the host today
because it is absent from that map. `AcpConversationState` has no role or owner field to tell the two apart
(`src/Weavie.Core/Sessions/AcpConversationState.cs:4-21`).

The record is also wider than a session id. The prompt-image receipt is a field of the per-visit owner, reset on every
open (`src/Weavie.Hosting/HostSession.WorkflowStageOwner.cs:17,63`; `docs/specs/conversation-layer.md:175-176`), and
the conversation's controls are saved nowhere (objection 7).

One owner removes the class: a host object per run and conversation that serializes open and close, holds the receipt,
and lends itself to one visit at a time, over a continuation the ACP store tags with the run and a role. `/clear` and
Rewind then forget it in the store that owns it, and the engine keeps holding no conversation identity.

### 5. "Nothing to reopen" is a fallback on the feature's only value

`AGENTS.md`, "No fallbacks": never paper over a failure with "a default that hides it"; "fail loudly at the surface
that meets the user". The brief opens a new conversation whenever the saved one cannot be reopened and "says so in the
transcript". The stage re-reads everything, which is the cost the feature removes, and the run carries on. The line
lands in a card that starts collapsed (`docs/specs/conversation-layer.md:66`), in a loop nobody is watching, and a
model decision then routes on that stage's summary.

The surrounding code treats the same conditions as failures. A lost stage conversation pauses the run
(`src/Weavie.Hosting/HostSession.WorkflowStageOwner.cs:79-81,121`). A saved session that cannot be restored throws
(`src/Weavie.AgentClientProtocol/AcpConversation.Opening.cs:26-29`). `docs/specs/workflow-agent-selection.md:100`:
"Nothing is substituted."

Two cases are folded into the row:

- **Expected absence**: a first visit, or the user removed the conversation with `/clear` or Rewind. A new conversation
  is correct.
- **Failed reopen**: a record exists and the restore fails. Forget the record and pause with the reason; Resume then
  opens a new conversation the user has agreed to.

The returning line can also be false. The turn is rendered and saved in the run before the host opens anything
(`src/Weavie.Workflows/WorkflowTransitions.cs:219-229`; the owner opens and then claims it,
`src/Weavie.Hosting/HostSession.WorkflowStageOwner.cs:59-73`). If the line is part of that turn and a new conversation
is substituted, the agent is told "your earlier work on it is above" with nothing above.

### 6. `reuse: <stage>` uses a stage id as a conversation's name

The brief rejects a named object because "a context is a conversation, so reuse is a value of `context`". The value is
another stage's id, which makes that stage the conversation's name, opener, and configuration at once:

- **The reuser arrives first** (Jump, or an `if` skip). It opens "the way the stage it names opens one", and the named
  `fresh` stage then makes its first visit into a conversation seeded by another stage's prompt.
- **The named stage is `fork`.** The brief re-forks on every visit because "an old copy lacks what main did since",
  then lets a reuser continue that old copy. Which conversation the reuser gets depends on how often the named stage
  has run.
- **Model by history.** A reuser with no `agent` "stays on the model the conversation already has"; one with an agent
  "switches in place". The model a stage runs on then depends on who held the conversation last, and each switch is a
  full uncached re-read (`docs/specs/workflow-agent-selection.md:185-187`).
- **Skill stages.** A skill's turn is its slash command (`src/Weavie.Workflows/WorkflowTransitions.cs:224-225`), and
  "the command stays the first block, which is how agents recognize it"
  (`src/Weavie.AgentClientProtocol/AcpConversation.PromptBuilder.cs:15`). "One factual line, then the stage's prompt"
  has nowhere to go.
- **`context` stops being an enum.** About a dozen sites branch on "not `continue`"
  (`src/Weavie.Workflows/WorkflowTransitions.cs:61,68,233,271`; `src/Weavie.Workflows/WorkflowRunner.cs:98-100,180,207,272`;
  `src/Weavie.Workflows/WorkflowStageBlock.cs:14`), and the host opens with `Context == Fork ? fork : fresh`
  (`src/Weavie.Hosting/HostSession.WorkflowStageOwner.cs:97-99`), where a third independent value takes the `fresh`
  branch unnoticed.

The workflow file already has a table of named things stages point at: `agents`. A `conversations` table beside it
(name, how it starts, its agent) says the same thing without the alias. The conversation belongs to the run and its
model to the conversation. The cost is a concept added to a vocabulary that is "deliberately small"
(`docs/specs/workflows.md:128`).

### 7. "Stays on the model it already has" matches no restore path

Every open applies the provider's defaults overlaid with the binding's hold
(`src/Weavie.AgentClientProtocol/AcpConversation.Controls.cs:9-12`). For a stage child the hold is a copy of main's
controls at that moment (`src/Weavie.AgentClientProtocol/AcpAgentSession.Conversations.cs:35-38`), then the stage's
agent (`src/Weavie.Hosting/HostSession.WorkflowStageOwner.cs:100-106`). A reopened aside takes main's live controls
(`src/Weavie.AgentClientProtocol/AcpNestedPorts.cs:60-62`). No child control value is saved
(`src/Weavie.Core/Sessions/AcpConversationState.cs:4-21`).

Through the existing path a return visit lands on main's current model. The brief's rule needs the child's values
saved with its continuation. Permission mode travels the same path and "workflows cannot select a different
permission mode" (`docs/specs/conversation-layer.md:89-90`): taken literally, the brief leaves a reopened child in a
looser mode after the user tightened main. Mode has to be copied from main on every visit, and the brief should say so.

### 8. One card at the first visit's position

- **Closed is terminal.** A stage child journals `side-conversation-closed` on every retirement
  (`src/Weavie.AgentClientProtocol/AcpAgentSession.Conversations.cs:100-101`). The pane's fold sets `terminal` on it
  and never clears it, so the card is neither active nor replyable whatever follows
  (`src/web/src/agent/AgentPaneSideConversations.ts:22-27,53-63`). The brief excludes "reworking how stage
  conversations appear in the pane", but "later visits continue in it" changes that fold regardless.
- **Position.** Only the first marker places a card (`src/web/src/agent/AgentPaneConversationRows.ts:49-51`), and rows
  carry the anchor fixed at first open (`src/Weavie.AgentClientProtocol/AcpNestedPorts.cs:26-29`). The third visit's
  work and approvals appear above every fix stage, far from where the transcript is growing.
- **Rewind.** Rows after the cut are kept when their anchor precedes it
  (`src/Weavie.AgentClientProtocol/AcpRewindPlan.cs:30`). Rewinding main to between visits keeps the later visits' rows
  and provider context, though main no longer reached them. Today each visit's card has its own anchor and is dropped.
  "A Rewind that keeps the card keeps the conversation" has no correct answer once visits share an anchor.

A card per visit over one conversation, which the brief excludes, gives Rewind a per-visit anchor and puts live work
where the user is looking.

### 9. Smaller points

- **Resume re-sends the prompt.** Resume reopens the stage with its full rendered work
  (`src/Weavie.Workflows/WorkflowTransitions.cs:130-140`). Into the reopened conversation that is a second copy, after
  a turn cut off mid-tool-call. The design should say what the agent is told.
- **Reversed decisions.** "Independently owned work has a readable transcript, never a resumable BTW continuation"
  (`src/Weavie.AgentClientProtocol/AcpAgentSession.Conversations.cs:87`); "never guessed resumable sessions" and no
  "automatic lost-child resumption" (`docs/specs/conversation-layer.md:172,208-209`). The brief lists these as doc
  edits. They are decisions the conversation layer made, and the design should argue each.
- **"One field table."** Choosing a context is also hand-written prose
  (`src/Weavie.Core/Mcp/AuthorWorkflowPrompt.cs:30-34`) and a table (`docs/specs/workflows.md:233-244`).
- **Credential on reload.** The real-agent check must confirm a reloaded session calls with the new visit's token, not
  the revoked one (`src/Weavie.AgentClientProtocol/AcpConversation.Opening.cs:176-194`); otherwise a return visit can
  never complete its stage.

## Alternatives

Definition and mechanism are separate choices.

**Definition**

| Option | Gains | Costs |
| --- | --- | --- |
| Brief: `fresh` reuses per stage; `reuse: <stage>` | Reuse with no YAML change | Objections 2 and 6 |
| B. Opt-in by stage: `fresh` and `fork` unchanged; `context: { reuse: <stage> }` may name its own stage | Existing workflows keep their meaning; a new conversation per pass stays expressible; unprofitable reuse is one removable line | One line per workflow that wants reuse; the stage-id alias remains for cross-stage reuse |
| C. Named conversations: a `conversations` table beside `agents`; stages say `context: <name>` | Everything in B; no alias, no "opens as another stage"; the model belongs to the conversation | A new top-level concept, schema rows, and authoring guidance |
| D. No engine change: the return stage is `fresh` with a narrow prompt (the findings file and only the commits since) | Zero code; bounded cost; independent eyes each pass; survives `/clear`, Rewind, cache expiry, and window limits | Lossy: the reviewer re-derives what the file does not say |

**Mechanism**

| Option | Gains | Costs |
| --- | --- | --- |
| Brief: close on leaving, restore on return | One path for return, unload, and restart; each visit's credential is fixed at open (`src/Weavie.AgentClientProtocol/AcpConversation.Opening.cs:176-194`) and revoked on leaving, so authority is by construction | Objection 1: endpoint succession, close-then-open ordering, and an unproven same-process reload; a full replay per visit on load-only agents |
| E. Keep open between visits; restore only after loss | What asides do today; the restore it falls back to (new process) is the tested one; no reload on the common path; no token difference, since caches are "keyed by prefix rather than by connection" (`docs/specs/ad-hoc-inference.md:25-27`) | Idle provider sessions for the run's length, cost unmeasured; two paths; authority by admission-time capture, as `continue` has on main (`docs/specs/conversation-layer.md:155-156`), not by a revoked credential; Rewind's rule for idle children (`docs/specs/conversation-layer.md:124-125`) needs a new answer |

D is the baseline for the measurement in objection 3. The 206k and 130k passes were clean full reviews; a clean pass
told to read only the fix commits and the findings may already beat a reused one, and it is available today.

## What holds up

- **Replay does not duplicate the card.** Updates replayed during a load are dropped before the pane
  (`src/Weavie.AgentClientProtocol/AcpConversation.Updates.cs:40-44`).
- **Per-visit authority, if reload works.** A grant per open, revoked on retirement
  (`src/Weavie.Hosting/HostSession.WorkflowStageOwner.cs:31-35,93`); a call captures visit and round
  (`src/Weavie.Workflows/WorkflowStageExecution.cs:25-26`).
- **No concurrent holders.** One execution per run and one stage owner per session
  (`src/Weavie.Workflows/WorkflowRunner.Execution.cs:53-58`; `src/Weavie.Hosting/HostSession.Workflow.cs:16`).
- **`fork` copies again on every visit**, and **no summary or redo instruction** in the returning turn, consistent with
  "no generated handoff summary" (`docs/specs/workflow-agent-selection.md:74-76`).
- **Unload and restart as the Resume case.** They already pause the stage
  (`src/Weavie.Hosting/HostSession.Workflow.cs:96-100`; `src/Weavie.Workflows/WorkflowRunner.cs:98-102`).
- **A run executes its saved definition** (`src/Weavie.Workflows/WorkflowRun.cs:63-64`), and **no reuse across
  providers** (`docs/specs/conversation-layer.md:146-148`).

## Recommendation

1. Measure first, with D as a baseline. If reuse does not beat D on billed tokens, change the workflow, not the engine.
2. Make reuse opt-in: B, or C if more than one or two stages will share a conversation.
3. Pick the mechanism from a real-agent probe of same-process close-then-reload. If agents refuse it, E is the only
   option. If they accept it, the brief's mechanism is defensible, with the connection-layer work in objection 1 in
   scope.
4. Either way: one host owner per run conversation over a tagged continuation in the ACP store, pause on a failed
   reopen, controls saved with the conversation and mode copied from main, and a reused conversation's usage reported.

## Not verified

- That the same-process reopen throws as read in objection 1; it was traced, not run.
- Whether a real Claude or Codex agent loads a session it closed in the same process, and whether a reloaded
  conversation reads from the cache.
- What the 260k, 206k, and 130k figures count, and which `context` PR #1194's review stages used.
- The context window of the Claude models the motivating workflow uses; only Codex's is recorded in the repository.
- Provider cache pricing and lifetime, and the memory cost of an idle provider session.
