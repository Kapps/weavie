# Workflow nested stages: brief

## Goal

A workflow stage can contain stages, and a stage can repeat once per entry of a list, so a workflow is a tree. The
workflow this must run: spec, design, then for each task group (in a copy of main: implement each task in a copy of
the group's conversation, review, open a PR, record proof, report), then a consistency check and an integration
review over the groups' reports.

A stage with child stages looks to its caller like any other stage: it has typed outputs and a summary, and the
caller cannot tell they came from children.

## Format by example

```yaml
stages:
  - id: design
    run: { prompt: ... }
    outputs: { groups: list }

  - id: group
    foreach: "{{design.groups}}"
    agent: opus
    context: fork                      # copies main as it stands when this entry starts
    outputs: { pr: "{{pr.pr}}", report: "{{report.report}}", videos: "{{prove.videos}}" }
    stages:
      - id: plan
        run: { prompt: List the tasks of "{{group.item}}" from tasks.md. }
        outputs: { tasks: list }
      - id: task
        foreach: "{{plan.tasks}}"
        context: fork                  # copies the group's conversation, on the group's agent
        run: { prompt: Implement "{{task.item}}". }
        outputs: { notes: text }
      - id: review
        agent: fable
        context: fresh
        run: { prompt: "Review this group's change set into review-{{group.index}}.md." }
        outputs: { findings: files }
        then: { ask: "Does {{review.findings}} list an open bug or warn?", choices: { clean: pr, fix: fix }, limit: 2 }
      - id: fix
        run: { prompt: "Fix the open findings in {{review.findings}}." }
        then: review
      - id: pr
        run: { prompt: Open this group's PR on top of the previous group's branch. }
        outputs: { pr: pr }
      - id: prove
        run: { prompt: Record proof. }
        outputs: { videos: files }
        then: { ask: Are the results ready?, by: user, choices: { ready: report, iterate: fix } }
      - id: report
        run: { prompt: "Write report-{{group.index}}.md: what this group built, its PR, review and videos." }
        outputs: { report: files }

  - id: consistency
    run: { prompt: "Read {{group.report}} and flag inconsistencies between groups." }
  - id: integration-review
    context: fresh
    run: { prompt: "Review how the groups in {{group.pr}} work together." }
```

## Decisions

| Topic | Decision |
| --- | --- |
| Stage shape | `id`, `if`, `foreach`, `agent` or `role`, `context`, `outputs`, `then`, and exactly one of `run` or `stages`. `run` holds only what to run: `prompt`, or `skill` with `args`. `agent`, `role`, `context`, and `outputs` move from `run` to the stage. |
| Outputs | A stage with `run` declares a type per output, and its agent reports the values. A stage with `stages` declares one reference to a child's output per output; Weavie copies the value with its type. A value its child never reported is absent, and reading it pauses the run, as any unreported value does today. |
| Summary | A stage with children has the summary of its last completed child. A report worth reading is written by a last child. |
| Root | The workflow is the root: `stages` only, in main, with no `agent`, `context`, or `outputs`. |
| Context | Relative to the parent. None: the enclosing conversation. `fork`: a copy of the enclosing conversation as it stands when the stage, or the entry, starts. `fresh`: an empty one. `{ reuse: <stage> }`: a sibling `fork` or `fresh` stage that is not repeated. A stage's own conversation runs its children that name no `context`. |
| Agent | A `fork` that names no agent runs on the model of the conversation it copies; a `fresh` one on the session's own, as today. Children with no `context` run on their parent's conversation, so one naming another agent or role is refused at load. An `agent` or `role` on a stage with children needs `fork` or `fresh`. A `run` stage in main switches main for that stage, as today. |
| `then` | A sibling, or `done`. `done` ends the current list of stages: the parent finishes and its `then` follows; at the root the run ends. No `then` means the next sibling, and after the last one `done`. A stage skipped by `if` goes to the next sibling. |
| Stage ids | Unique across the file. |
| References | A stage reads its siblings' outputs, everything its parent can read, and the `item` and `index` of every enclosing `foreach`. A stage with children also reads its own children's outputs, in `outputs` and `then`. Nothing else reads another stage's children. Checked at load. |
| Reports | A stage with its own conversation reports to its parent's conversation: one message with its outputs and summary, as an independent stage reports to main today. |
| `foreach` | One reference to a `list` or `files` value, captured when the stage starts. Entries run in order in the session's one worktree. Each entry is a new instance with its own conversations. An empty list skips the stage. |
| Current entry | `{{<stage>.item}}` and `{{<stage>.index}}` (from 1), readable only inside that stage. Neither is allowed as an output name on a repeated stage. |
| Repeated outputs | From outside, an output holds one value per entry, each with its type. A prompt shows them labelled by entry. As a `foreach` list or as decision evidence they are one flat list. |
| Decisions on a repeated stage | Its own `if`, `then`, and `limit` apply once to the whole repetition. A decision inside it counts `limit` per entry. A stage's `limit` count is not reset by its children's decisions. |
| Returning | A `then` back to a stage with children, from a sibling or from itself, enters it again at its first child: a `fresh` conversation continues and a `fork` copies again, as for any stage today. A `then` back to a repeated stage starts from its first entry with a newly read list and new conversations. |
| Conversation lifetime | Everything opened inside an entry closes when that entry finishes. Every other conversation stays open until the run ends. Closed cards stay readable. |
| Lost conversation | A stage whose own conversation cannot be restored after its children have started pauses the run, naming what was lost. Resume continues in a new conversation. A `run` stage keeps today's rule: a new conversation and a line in main. |
| Pause and Resume | A pause sits at one stage or decision in one entry, and Resume restarts that. `failStage` pauses; no entry is skipped. |
| Jump to Stage | A sibling of the current stage, or `done`. A sibling with children starts at its first child, a repeated one at its first entry. |
| Questions | Every workflow question is asked in main and carries the asking stage's outputs and summary. Only the answer to that question is accepted. An agent's own question during its work shows in its card, as today. |
| Header and session list | The stage path with each repeat's entry and total: `feature-build · group 2/5 › review`. |
| Stage block | The same path with each entry's text, in place of "(1 of 10)". |
| Cards | One per conversation, flat in main's transcript where it started, titled by path (`group 2 › task 3`). |
| Terminal Claude | Runs nesting and `foreach` in main. `fork` and `fresh` stay refused. |
| Switch-over | No compatibility. Schema, engine, docs, `author-workflow`, and the built-ins change together. The build writes validated copies of the personal workflows (`feature-build`, `issue-fix`, `quick-fix`) to the run's shared documents folder; the user installs them once the new build is live. |

## Edge cases

- A run saved by the old build is set aside with a message when the new build loads it, and is started again.
- A run whose saved definition no longer validates cannot be cancelled or replaced today. This build fixes that.
- A fork taken before its parent's conversation has had a turn would miss reports still owed to that conversation,
  so its opening prompt carries them. A task fork therefore sees the earlier tasks' reports, not their work.
- By its second entry a repeated `fork` also holds the earlier entries' reports and the user's answers to their
  questions. That is intended: a later group knows what the earlier ones did.
- Lists hold text only. A list per entry comes from a child that reports it (`plan` above).
- Entries share the worktree, so a file each entry writes needs the entry in its name (`{{group.index}}`).
- A decision reads a file's contents only when its question references the output, as today.
- Jump to `done` from an unfinished entry leaves that entry's outputs absent.
- A fault found after a repetition is fixed by a stage outside it: the run cannot return to one entry. A broken
  entry is resumed, or the run is cancelled.
- The example leaves about 30 cards in main's transcript.
- Cancel closes every conversation at every depth.

## Exclusions

- A stage that runs in its own session. It is the next feature; nothing in the format or the saved run may assume a
  stage runs in this session.
- A stage with both `run` and `stages`, and a closing turn for a stage with children.
- Parallel entries, and a worktree or branch per stage. Stacked PRs are the workflow's prompts.
- A stage that runs another workflow file.
- A repeated `fork` that copies a snapshot taken when the repetition started.
- Folding a finished entry's cards. Follow-up PR.
- Restarting at a chosen entry, skipping an entry, and Jump beyond siblings and `done`.
- Nested lists and structured entries.
- Requiring every `then` to be written.
- Stage ids scoped to a parent.
- Prompt cache lifetime settings.
- Reshaping `feature-build` beyond moving the three fields.

## Approach

- **Definition.** A stage holds stages. The schema, the reader, and the load-time checks (ids, `then` targets,
  reference visibility, output references, `reuse`, the agent rules, reserved names) change together with
  `workflows.md`, the `author-workflow` prompt, and the built-ins.
- **Run.** The run saves one record per list of stages it is inside. A record has its own id and owns its
  children's outputs, its `limit` counts, the reports owed to its conversation, its captured list and entry, and
  its position. The root is the first record. A repeated stage gets a new record for every entry of every pass; any
  other stage with children keeps one for as long as its parent's record lives. The stage path is derived for
  display and identifies nothing.
- **Engine.** The same state machine works on the innermost record: entering a stage with children opens a record,
  finishing its list copies the outputs and follows the parent's `then`, and finishing an entry discards its record
  and starts the next.
- **Conversations.** Each turn carries the conversation it runs in, resolved once when its record opens: main, or
  the record that owns it. No code reads "no `context`" as "main". Each kept conversation keeps its one owner,
  identified by its record, and discarding a record closes what it owns. A fork is requested from the conversation
  it copies.
- **Display.** The header, session list, stage block, and card titles show the derived path.
- **First build step.** Check on real Claude and Codex: a fork of a fork, and of that fork; a fork of a
  conversation that has had no turn of its own; restoring a fork of a fork after an agent restart; forking a parent
  again after its earlier children closed; forking a parent whose turn is still ending; and how many sessions one
  agent process holds. A refusal goes back to the user before the rest is built.

Reasons:

- A record per instance gives per-entry outputs, limits, and lifetimes one place. A path over one flat record would
  reuse a conversation's identity on a second pass, where restore runs before open and would continue the first
  pass's reviewer ([challenge.md](challenge.md) objection 1, and an independent challenge by a second model).
- `run` or `stages`, not both: a closing turn had a model retype values Weavie already holds, where a wrong PR URL
  passes validation; changed what `agent` and a `then` back to itself mean on a stage with children; and had no
  single turn to reopen on a repeated stage. Copied outputs cost one line each and no turn.
- `agent`, `context`, and `outputs` sit on the stage because they describe the stage, whichever of `run` or
  `stages` it has.
- An entry's conversations close with it because nothing can return to a finished entry, and groups times tasks
  would otherwise stay open for the whole run.
- A repeated `fork` copies the live conversation because a later group should know the earlier ones' results, and
  it needs no retained fork point.
- One value per entry keeps which report belongs to which group, and lets a multi-line text be an entry.
- A lost parent conversation pauses because a silent replacement would continue without the plan and the work its
  children reported into it.
- One conversation per group, a clean start per group, and a reviewer with no shared context follow the published
  evidence: [long context degrades every model tested](https://hamel.dev/notes/llm/rag/p6-context_rot.html),
  [split context breaks coupled work](https://cognition.ai/blog/dont-build-multi-agents), and
  [review works best with none shared](https://cognition.com/blog/multi-agents-working). The format lets the author
  choose `fork` or `fresh` at each level.
- File-wide ids let Jump, the header, and cards name a stage with one word. Scoping them to a parent later breaks
  nothing.
- Sessions wait because a stage with children is a sealed unit with its own record: where it runs can become one
  more property without changing what its caller sees.
