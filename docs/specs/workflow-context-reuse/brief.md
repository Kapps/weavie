# Workflow context reuse: brief

## Goal

A workflow stage that runs in its own conversation keeps that conversation for the whole run. A return visit
(a re-review after a fix) continues where the stage left off instead of re-reading the design, the standards, and the
code in a clean context. In PR #1194 three review passes each paid for a clean context: about 260k, 206k, and 130k
tokens.

## Decisions

| Topic | Decision |
| --- | --- |
| `fresh` | One conversation per stage per run: opened on the stage's first visit and continued on every later visit, however the run arrives (a `then` loop, another stage, Jump to Stage). There is no opt-out. |
| `fork` | Copies main again on every visit, as today. A fork exists to carry everything so far, and an old copy lacks what main did since. |
| `context: { reuse: <stage> }` | Continues the conversation the named stage last opened, for a return that needs its own prompt or `limit`. The named stage must be `fork` or `fresh`; anything else is refused at load. Any number of stages may reuse one conversation. |
| Model | The conversation runs on the agent of the stage that opened it. A reusing stage that names an `agent` is refused at load, so a conversation never switches model mid-run. Permission mode is copied from main at the start of each visit. |
| Lifetime | The conversation stays open between visits and behaves as a BTW aside does: after a host restart, a session unload, or Restart Agent the next visit restores it, and an agent crash ends it. |
| Between visits | The card behaves as an aside: the user can reply in it while it is live. A reply carries no stage authority. |
| Resume | Resume after a conversation is lost mid-visit restores that conversation, for every `fork` and `fresh` stage, so the stage does not start over. |
| Nothing to continue | When a visit finds no conversation (a reusing stage arrives first, the user removed it, or the restore fails), Weavie opens a new one the way the opening stage does and says so, with the reason, in main's transcript. The run does not pause. |
| Returning turn | The stage's prompt rendered with current values and any user feedback. The stage block adds one factual line (`Stage "review" has opened again; your earlier work on it is above.`) only when the turn continues an existing conversation. Weavie adds no instruction about what to redo and no summary of what happened in between. |
| Card | One card per conversation. It stays where it started in the transcript and later visits continue in it. |
| Jump to Stage | An arrival like any other: it continues the conversation. |

## Edge cases

- `/clear` and Rewind get no workflow handling: they close stage conversations as today, and the next visit opens a
  new one.
- After a host restart a kept conversation receives the run's prompt images once more.
- A finished or cancelled run closes its conversations. Their cards stay readable.
- A run executes the definition snapshot it started with, so an edited workflow file needs no handling.
- A visit's completion authority is captured when a tool call is admitted, as main's is for `continue`. A call made
  between visits, or delayed from an earlier visit, cannot complete a stage.
- Prompt images a conversation has already received are not sent again on a return within one host lifetime.
- Resume sends the stage's prompt into a conversation that already holds it. The design says what the agent is told.
- The conversation grows with every visit. Compaction stays the agent's job; a returning turn the model's window
  cannot hold fails and pauses the run, as any failed turn does.
- Every request in a returning visit carries the earlier conversation, at the cached price while the provider's cache
  holds and uncached after. Stage conversations report no usage today, so the saving is unmeasured.

## Exclusions

- A way to demand a new conversation on a later visit.
- Closing a conversation on leaving its stage and reopening it on return. No code reopens a session on a running
  agent process, and no agent is known to allow it.
- A named `conversations` table.
- Reworking how stage conversations appear in the pane, including a card per visit.
- A cap, Weavie-run compaction, or token reporting for a reused conversation.
- Setting the provider's cache lifetime. ACP has no field for it; each agent has its own launch-time setting.
- Reuse across providers.

## Approach

- **Definition.** `context` accepts the `reuse` mapping beside `continue`, `fork`, and `fresh`. The schema, load-time
  diagnostics, the `author-workflow` prompt, and the Choosing context table change together.
- **Owner.** One host object owns each kept conversation for the run. It opens or restores the conversation, holds
  the prompt-image receipt, lends the conversation to one visit at a time, and closes it when the run ends. A visit
  borrows it as a `continue` stage borrows main. The engine resolves which stage's conversation a visit uses and
  stores no conversation identity.
- **Conversation layer.** A stage conversation is saved, suspended, and restored by the paths asides use, tagged as
  the run's so that only its owner restores it. Closing it forgets it.
- **Docs.** `workflows.md` (Defaults, Choosing context, "leaving the stage closes it"), `workflow-agent-selection.md`,
  and `conversation-layer.md` change with the code. The design argues each conversation-layer decision it reverses:
  stage children as display-only rows, and no automatic lost-child resumption.

Reasons:

- A context is a conversation, so reuse is a value of `context`, not a second field or a named object.
- Keeping the conversation open is what asides do, and restoring after the process is replaced is the path main and
  asides already exercise, so the feature adds no new protocol behaviour.
- One owner gives the conversation's lifetime, record, and authority a single place, so the run and the store cannot
  disagree about whether it exists.
- The workflow's prompt says what a return visit does, because only its author knows.

The first build step measures billed tokens on real Claude and Codex agents for a review, fix, re-review loop: the
reused conversation against a clean recheck told to read only the findings file and the fix commits.
