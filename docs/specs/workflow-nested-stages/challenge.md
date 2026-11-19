# Workflow nested stages: challenge

First visit; there are no earlier objections to account for. The format (a stage holds stages, `foreach` on a
stage, lexical references) holds up. The objections are to how the brief's Approach stores and addresses an
instance of a stage, and to the closing turn. Strongest first.

## 1. An entry is an owned thing; the brief stores it as a path over one flat record

The Approach keeps one `WorkflowRun`, turns its position into "a path of stages with an entry number at each
repeat", and identifies a conversation by "the run, the stage that opened it, and the entry number of each
enclosing repeat". Every per-entry rule in the Decisions table (outputs per entry, `limit` per entry, conversations
that close with the entry, new conversations on a restarted repetition) is then a rule each call site must apply
to a shared record. The code shows where that fails.

- **A restarted repetition reuses its keys, and restore runs before open.** Kept conversations are found by
  `AgentConversationKey(Scope, Name)` (`src/Weavie.Core/Agents/IAgentConversation.cs:54`), today
  `(runId, opener.Id)` (`src/Weavie.Hosting/HostSession.WorkflowConversation.cs:31`). An owner with no live
  conversation calls `RestoreAsync` by that key before it opens anything
  (`src/Weavie.Hosting/HostSession.WorkflowConversation.cs:107-115`), and restore returns the first kept state
  with that owner (`src/Weavie.AgentClientProtocol/AcpAgentSession.Conversations.cs:23`). "A `then` back to a
  repeated stage starts it from its first entry with ... new conversations" produces the same
  `(run, review, group 1)` key on the second pass. Owners exist only in memory and only once a turn is delivered
  (`src/Weavie.Hosting/HostSession.Workflow.cs:49-51,85-93`), so after a host restart mid-entry a kept `fresh`
  conversation that takes no further turn has no owner to close it; the next pass restores the previous pass's
  review. The only forgetting rule today is "another run's" (`src/Weavie.Hosting/HostSession.Workflow.cs:88,103`).
- **An outer `limit` is erased by inner decisions.** A model answer replaces the whole streak table with one
  entry: `Streaks = new() { [run.Stage] = count }` (`src/Weavie.Workflows/WorkflowTransitions.cs:120`), and a
  user answer clears it (`:91`). A stage with children that loops on its own `then` passes through its children's
  decisions on every pass, so its count never exceeds 1 and its `limit` never fires. The brief says only that an
  inner decision "counts `limit` per entry".
- **Entry end has no signal.** Hosting learns that conversations should close by comparing run ids inside a
  parameterless `Workflow.Changed` handler (`src/Weavie.Hosting/HostSession.Workflow.cs:97-104`). With paths it
  would diff two positions to work out which entries ended, at each place that cares.
- **It contradicts the brief's own exclusion.** "Nothing in the format or the saved run may assume a stage runs
  in this session." One record holding every level's outputs, streaks, and owed reports, addressed by path, is
  exactly that assumption: a stage's state cannot move to another session without cutting it out of the record.
- **It reverses a settled rule.** `docs/specs/conversation-layer.md:84`: "IDs remain at protocol, persistence, and
  UI boundaries; the workflow owner retains an object." `docs/specs/workflow-context-reuse/brief.md:59-60`: "The
  engine ... stores no conversation identity."

**Better: persisted frames.** The run holds a stack of frame records. Each frame carries a minted instance id
(new per entry and per pass), its stage, the captured list and entry index, its current child or a closing-turn
marker, its children's outputs, its streaks, and the reports owed to its conversation. The root run is the first
frame, so today's `Outputs`, `Streaks`, `Summary`, and `Unreported` (`src/Weavie.Workflows/WorkflowRun.cs:94-106`)
move into it unchanged in meaning. `WorkflowTransitions` stays pure and operates on the top frame; leaving a frame
is an effect beside `EnterStage`, and the runner exposes a retirable scope object rebuilt from the stack on load,
as `_execution` is (`src/Weavie.Workflows/WorkflowRunner.cs:89`,
`src/Weavie.Workflows/WorkflowStageExecution.cs:23-26`). Hosting keys owners by frame id as the key's `Scope` and
ends them by awaiting the scope, as it awaits `visit.Retired` today
(`src/Weavie.Hosting/HostSession.WorkflowConversation.cs:181`). The architect, consulted on this one decision,
reached the same verdict.

| | Path over a flat record (brief) | Persisted frames |
| --- | --- | --- |
| Per-entry outputs, `limit`, lifetimes | A rule at every call site | Discarding the frame |
| Restarted repetition | Same keys as the last pass | New ids, no collision |
| Header and card titles | The stored truth | A projection of the stack |
| A stage in its own session, later | Cut state out of the record | Move a frame |
| Cost | Smaller first diff | `WorkflowRun` and the transitions change shape up front; the 2,700 lines of engine tests move with them |

A third option, child runs as `docs/specs/workflows.md:195` and its "Child runs" section already describe
(`workflow:` plus `foreach`, `in: here`), gets frames for free because a run already is one, but needs argument
passing and one file per level. The brief is right to prefer inline `stages`; frames are that design's state
with the brief's syntax.

## 2. "Which conversation" is written relative, and read as absolute in about twenty places

The brief makes `context` relative to the parent: none means "the enclosing conversation". The code reads
`Context == Continue` as "this is main" everywhere, and a child with no `context` inside a `fork` parent breaks
each reading:

- `CaptureStageCall` hands completion authority to main when the stage's context is `Continue`
  (`src/Weavie.Workflows/WorkflowRunner.Execution.cs:15`), so main could complete a stage running in a group's fork.
- `Complete` skips the report and returns the user's question in the tool reply for a `Continue` stage
  (`src/Weavie.Workflows/WorkflowTransitions.cs:57,64`); the brief wants that question asked in main.
- `Send` and `TryClaimKickoff` attach and clear main's owed reports, and mark main as having the prompt images,
  for any `Continue` turn (`src/Weavie.Workflows/WorkflowTransitions.cs:267`,
  `src/Weavie.Workflows/WorkflowRunner.cs:284`).
- `DeliverWorkflowTurn` sends a `Continue` turn to main's queue (`src/Weavie.Hosting/HostSession.Workflow.cs:49`),
  and `WorkflowControlHold` applies main's held model to it (`src/Weavie.Hosting/HostSession.WorkflowAgent.cs:167`).
- The stage block, reload, and terminal refusal ask the same question the same way
  (`src/Weavie.Workflows/WorkflowStageBlock.cs:17`, `src/Weavie.Workflows/WorkflowRunner.cs:90,185,212`,
  `src/Weavie.Core/Workflows/WorkflowAgentControls.cs:27`).

The missing abstraction is the resolved conversation of a visit: main, or the frame that owns it. Resolve it once
when the frame is entered and carry it on the visit and the kickoff in place of `WorkflowKickoff.Context`
(`src/Weavie.Workflows/WorkflowRun.cs:128`), so no site re-derives it from a relative word.

The fork seam has the same flaw. The only way to fork is `OpenForkAsync(binding)`, documented as "Forks the
current main incarnation" (`src/Weavie.Core/Agents/IAgentConversation.cs:61`), which builds
`new ForkFromOpening(primary, …)` (`src/Weavie.AgentClientProtocol/AcpAgentSession.Conversations.cs:14-15,99-101`).
The runtime already accepts any live parent (`src/Weavie.AgentClientProtocol/AcpRuntime.cs:63`). Fork belongs on
the handle the parent frame holds (`IAgentConversation`), not on the session with a key or a parent argument;
`OpenForkAsync` stays for main, which `/clear` and Rewind replace.

Two things the brief leaves open follow from this:

- **Which agent an agent-less nested fork runs on.** A child's controls are snapshotted from main
  (`src/Weavie.AgentClientProtocol/AcpAgentSession.Conversations.cs:80`), after main is returned to the session's
  own values (`src/Weavie.Hosting/HostSession.WorkflowConversation.cs:74`). In the example `task` forks a group
  running on `opus` and names no agent. The brief does not say whether it runs on `opus` or on the session's model.
- **Restore order.** After a host restart, a child fork needs its parent restored and live before it can fork
  again; the frame stack has to drive that order.

## 3. The closing turn makes "one kind of stage" true in name only

The brief rejects a group kind so that there is one kind of stage. With `stages` present, five fields change
meaning:

| | Stage without children | Stage with children |
| --- | --- | --- |
| `run` | The work | A report written after the work |
| `agent` in main | The model that does the work | The model of the closing turn only; "never inherited by children in main" |
| `agent` on `fork`/`fresh` | The stage's model | The model of every child with no `context` |
| `then` back to itself | Revise the work (`src/Weavie.Workflows/WorkflowTransitions.cs:172-181`: "Revise the work accordingly") | Reopen the report; "its children do not run again" |
| Resume, Jump | Restart the work | Restart the report; start at the first child |

That is a group kind with an extra turn. Its costs:

- **A model retypes values the engine already holds.** In the example the group's `pr`, `report`, and `videos`
  are `pr.pr`, `review.report`, and `prove.videos`, already validated. `docs/specs/workflows.md:50` puts typed
  outputs with Weavie, not the agent. A retyped `pr` is checked only as an http(s) URL
  (`src/Weavie.Workflows/WorkflowValues.cs:48-56`), so the wrong PR passes.
- **The closing turn cannot be given its children's values.** "It cannot read the outputs of another stage's
  children", and a stage reads only "its siblings' outputs, everything its parent can read". So the group's `run`
  and its `then.ask` cannot reference `{{review.report}}`; a model decision on a stage with children gets file
  evidence only through a re-reported `files` output. The brief should say whether a stage reads its own children.
- **Two decisions contradict each other for a repeated stage.** "A stage whose own `then` leads back to itself
  reopens its closing turn" and "A `then` back to a repeated stage starts it from its first entry". A repeated
  stage has one closing turn per entry and each ran in a conversation that closed with its entry, so there is no
  closing turn to reopen.
- **Jump to `done` inside an entry lands on a report with nothing to report.** `done` "ends the current list of
  stages: the parent's closing turn starts". That turn must still report every declared output; a missing one or a
  `files` value that does not exist is rejected (`src/Weavie.Workflows/WorkflowTransitions.Values.cs:46-48`,
  `src/Weavie.Workflows/WorkflowValues.cs:63-66`) and the run pauses after `workflow.maxStageAttempts`
  (`src/Weavie.Workflows/WorkflowTransitions.cs:47-51`). The one way out of a bad entry is a stuck one.

**Alternative:** a stage has `run` or `stages`, not both. A stage with `stages` declares outputs as references to
its children (`outputs: { pr: "{{pr.pr}}" }`), type-checked at load by the existing resolver
(`src/Weavie.Workflows/WorkflowReferences.cs:4-8`); its summary is its last child's, which is already what
`Summary` means (`src/Weavie.Workflows/WorkflowRun.cs:96-97`). Work that must close the group is the last child,
the mirror of the brief's own "work that must precede the children is the first child".

| | Closing turn (brief) | `run` or `stages`, mapped outputs |
| --- | --- | --- |
| Kinds of stage | One in the schema, two in behaviour | Two, stated |
| Group outputs | Retyped by a model, one turn per entry | Copied by the engine, no turn |
| A synthesized report | Free | Written as a last child |
| Special rules | `then` to self, Resume, Jump, `agent`, own-child references | None |
| New syntax | None | A reference as an output value |

The brief lists "a stage with children and no `run`" as an exclusion without a reason beyond "there is no output
mapping". That mapping is one line per output and removes the table above.

## 4. Flat lists lose what the consumers need

- **Entry association.** `{{group.report}}` joins every entry's files and `{{group.pr}}` lists the PRs, with no
  way to tell which report belongs to which PR once an entry reports two files. `consistency` and
  `integration-review` are the two consumers and both need the pairing.
- **Types.** There are five value types and none is a list of `pr` or `url`
  (`src/Weavie.Workflows/WorkflowDefinition.cs:71-86`); a repeated `pr` becomes a plain `list` and loses its check.
- **Text entries do not fit list items.** A `text` output accepts any string, including an empty one
  (`src/Weavie.Workflows/WorkflowValues.cs:40-46`); a list item must be non-blank (`:78`), and a list renders one
  `- item` per line (`:17`), so the example's multi-line `notes` become broken bullets and an empty one breaks
  "one item per entry".
- **The loop cannot read its own progress.** The example's `pr` stage says "on top of the previous group's
  branch", and the visibility rule gives a child no way to read `{{group.pr}}` so far. It works only because the
  previous group's report is still owed to main and is copied into the next fork's opening prompt. That is state
  carried by a prompt, the thing `docs/specs/workflows.md:46-50` exists to avoid.

Options: render a repeated output as one labelled block per entry (`item`, then its values) and keep the flat list
only for `files` evidence; and let a stage inside a repetition read the entries finished so far.

## 5. Recovery in a long run is all or nothing

The example runs five groups with a PR each. From a failed entry the exits are Resume (the stage starts again),
Jump to a sibling, Jump to `done` (objection 3), or Cancel. A `then` back to the repeated stage redoes every entry
"with a newly read list and new conversations", so an integration review that finds a fault in group 3 cannot send
the run back to group 3. The brief excludes restarting at an entry and skipping one. That is a defensible first
cut only if the brief states the consequence: findings after the repetition are fixed by a stage outside it, and a
broken entry is resumed or the run is cancelled.

## 6. The first build step checks too little

It checks that a fork of a fork opens. Resume depends on more, and each should be in the same check on real
Claude and Codex before the rest is built:

- `session/load` of a fork of a fork after an agent restart. Forking at all requires `Fork` and `Load`
  (`src/Weavie.AgentClientProtocol/AcpAgentSession.cs:87-89`), and restore requires `Load` or `Resume` and an
  unbound session (`src/Weavie.AgentClientProtocol/AcpAgentSession.Conversations.cs:24`).
- A fork taken from a parent whose last turn is still ending, since a child completes from inside the parent's
  turn and the next child forks at once.
- Closing a parent's children, then forking the parent again, for the second task.
- How many sessions one agent process holds open: a group, its review, and a task are open at once.

## 7. Smaller points

- **Cards.** A card is placed after a main turn (`src/Weavie.Core/Sessions/AcpConversationState.cs:9-10`). Main
  takes no turn during a repetition unless a question is asked, so most of the example's 30 cards sit after the
  same turn, and Rewind drops kept conversations by that anchor
  (`src/Weavie.AgentClientProtocol/AcpAgentSession.Rewind.cs:68`). Grouping cards by frame is part of making 30
  cards usable, not a follow-up.
- **Stage block.** Dropping "(1 of 10)" is right; the header already omits a count for the same reason
  (`docs/specs/workflows.md:514`).
- **Switch-over message.** A run saved by the old build is more likely set aside than paused: `WorkflowRun`'s
  members are `required` (`src/Weavie.Workflows/WorkflowRun.cs:79-118`), a document missing new ones fails to
  read, and the store backs it up and reports "couldn't be read ... it was set aside"
  (`src/Weavie.Core/Workflows/WorkflowRunStore.cs:55-58`). There is then no run to cancel. The edge case should
  name the outcome that happens.
- **Flat scans.** Every `definition.Stages` lookup assumes one list (`src/Weavie.Workflows/WorkflowDefinition.cs:26-52`,
  `src/Weavie.Workflows/WorkflowRunner.cs:245`, `src/Weavie.Workflows/WorkflowReferences.cs:7`,
  `src/Weavie.Workflows/WorkflowSemantics.cs:10-22`, `src/Weavie.Core/Workflows/WorkflowAgentControls.cs:26`). One
  tree index on the definition replaces them; file-wide ids make it a dictionary.

## Verdict

Keep the format. Change the Approach in two places before design: store instances as persisted frames with minted
ids and a resolved conversation (objections 1 and 2), and decide the closing turn again with the mapped-outputs
alternative on the table (objection 3). Objections 4 to 6 are gaps to close in the brief; none needs a different
approach.
