# Workflow nested stages: review

Review of [PR #1287](https://github.com/Kapps/weavie/pull/1287) against [design.md](design.md).

## Baseline

- Head `6e67012f`, based on `origin/main` `d199aeb5`. Product code: 46 files under `src/`, 1,487 lines added and 575
  removed; the rest of the diff is tests, e2e, and docs.
- One pass by `weavie-reviewer` over the design and the branch diff, covering design conformance, correctness,
  duplication, repo standards, and test quality. Every finding below was then checked against the code at head.
- Checked by running the engine (`temp/validate` on the files in `temp/review-1287/probes/`): B1, B3, W6, N1, and
  the validation half of B2. Everything else was checked by reading and tracing the code. Nothing was run on the
  host or agent layer for this review.
- Nothing is fixed here. CI on the PR was pending when the review was written.

## Recheck

- Head `68b206ad` (one fix commit over the reviewed `6e67012f`). Only that commit was read; the design changed in two
  lines (an empty or unreported repeated output is present with no entries), which is not a change of scope.
- Rerun on `68b206ad`: `Weavie.Workflows.Tests` 298 passed; `Weavie.Hosting.Tests` filtered to
  `Acp|Side|Rewind|Btw|Workflow|Aside|DecisionProvider|Steer` 493 passed; `Weavie.Core.Tests` filtered to
  `Workflow|RegistryMcp` 120 passed. The engine probes were rebuilt and rerun: B1, B3, W6, and N1 now report as the
  fixes say. e2e was not rerun here. CI on `68b206ad` was still running.
- The no-steer assertion that stands in for "the next stage's turn waits" fails three runs out of three with the
  idle wait removed, so it does test the wait.
- Every fix marked fixed below held, except N11. Two fixes had side effects, recorded as B5 and W10; N19 is a wording
  change that came with W7.
- W8's three rejected tests are accepted: record ids on a second pass are proven in the engine, the e2e spec answers
  a question from inside a group through the host, and the real-agent probe restored nested forks.

## Second fix pass

B5 and W10 are fixed; see the verification record for the head. N11 and N19 stay open: the second pass did not
touch their code.

## Verdict

One bug (B5) and one warning (W10) are open, both introduced by the fix pass. Open nits: N4, N6, N7, N11, N12, N14 to
N17, N19.

## Bugs

| Id | Status | Where | Finding |
| --- | --- | --- | --- |
| B1 | fixed: `Opener` falls back to the stage when its reuse names no sibling; `WorkflowTreeSemanticsTests.AReuseNamingNoSiblingIsReportedEvenWhereAnAgentAsksForItsConversation` | `src/Weavie.Workflows/WorkflowSemantics.cs:111` | Validating a workflow throws where it should report. `Opener(parent)` runs `First` over the siblings (`WorkflowDefinition.cs:70`), so a stage with stages whose `context.reuse` names no sibling, holding a child with `agent` or `role` and no `context`, throws `InvalidOperationException` out of `WorkflowReader.Read`. `WorkflowCatalog` catches only IO exceptions, so the file breaks listing and `validateWorkflow`. |
| B2 | fixed: `TryResolve` reads run values and inputs; `ARepeatedStageReadsInputsAndCopiedRepeatedOutputs` (an absent optional list input skips its stage) | `src/Weavie.Workflows/WorkflowTransitions.Values.cs:85` | `foreach` over an input list validates, then pauses forever. `TryResolve` reads through `Lookup`, which knows stage outputs and entries only, so `foreach: "{{inputs.items}}"` pauses with `stage "inputs" hasn't reported it` and Resume pauses the same way. `CheckForeach` accepts it because `TypeOf` resolves inputs. |
| B3 | fixed: `WorkflowReferences.IsRepeated` follows copied outputs; `ARepeatedOutputCopiedByAStageWithStagesIsAList` and the engine run in `ARepeatedStageReadsInputsAndCopiedRepeatedOutputs` | `src/Weavie.Workflows/WorkflowSemantics.cs:139` | `foreach` over a copied repeated output is rejected. `repeated` asks only whether the referenced stage itself repeats, so a stage that is not repeated and copies a repeated child's text output gets `{{g.notes}} is not a list`, though its value is an `Entries` value `Flat()` handles. The design allows "a `list`, `files`, or a repeated output" and names a `WorkflowReferences` helper for it that does not exist. |
| B4 | fixed: `SourceAsync` reuses a live conversation only when this pass already used it, else opens a new one and records it through `WorkflowRunner.MarkOpened`; `AForkThatRunsFirstInAGroupForksThisPassesGroupWhichItsStagesThenContinue` | `src/Weavie.Hosting/HostSession.WorkflowConversation.cs:114` | A fork that runs first in a re-entered fork group copies the previous pass's conversation. `SourceAsync` returns any live conversation without asking `HasOpened`. With `g: fork`, stages `[t (fork), r]`, and a `then` back to `g`, the second pass has `g` dropped from `Opened` (`WorkflowTransitions.Tree.cs:11`) but its first conversation is still live, so `t` forks it with the first pass's history, and `r` then gets a new fork of main. Breaks "a fork entered again copies again". No test has a fork child that runs before its group's own first turn. |
| B5 | fixed: `IsRepeated` follows a copy only from one of the stage's own stages, so the walk descends the tree and ends; `CopiedOutputsThatCopyEachOtherAreReportedNotFollowed` covers both probes | `src/Weavie.Workflows/WorkflowReferences.cs:14` | A workflow file whose copied outputs form a cycle overflows the stack and kills the process. `IsRepeated`, added for B3, follows an output's source with no bound, so a stage with stages that declares `n: "{{g.n}}"` on itself, or two stages copying each other, named by a `foreach`, recurses without end. A stack overflow cannot be caught, and `WorkflowCatalog` reads every workflow file through this path when it lists them, so one such file in a repo ends the worker. Reproduced with `temp/review-1287/probes/r1.yaml` and `r3.yaml`. |

## Warnings

| Id | Status | Where | Finding |
| --- | --- | --- | --- |
| W1 | fixed: same change as B4; the same test asserts one group card per pass and that the group's own stage continues the conversation its task forked | `src/Weavie.Hosting/HostSession.WorkflowConversation.cs:80` | Same cause as B4, on the first pass. A group conversation opened as a fork's source never took a kickoff, so the group's first own turn arrives with `Continues` false and `EnsureAsync` closes it (line 129) and opens another. A `g` card opens and closes empty, and the tasks forked a different conversation from the one the group's later stages run in. |
| W2 | fixed: a renewed conversation that the pass had used always gets the line; `AGroupWhoseConversationIsLostPausesAndResumeOpensANewOne` asserts it | `src/Weavie.Hosting/HostSession.WorkflowConversation.cs:148` | No line in main when a lost group conversation is renewed and `RestoreAsync` returned null without throwing (Rewind, `/clear`): `renewing` leaves `lost` null. The design's error table says Resume "opens a new one with the line in main". `AGroupWhoseConversationIsLostPausesAndResumeOpensANewOne` takes this path and does not assert the line. |
| W3 | fixed: `ConversationOwner` reads the live records before taking the owners' lock | `src/Weavie.Hosting/HostSession.Workflow.cs:92` | Lock order inverted. `LiveWorkflowFrames()` takes the runner's lock while `_workflowConversations` is held; the `Changed` handler (line 104) runs under the runner's lock and then takes `_workflowConversations`. `OpenAsync` calls `ConversationOwner` from a pool thread, so the two can deadlock when the owners dictionary is empty during a commit. The window is narrow. |
| W4 | fixed: a steer's end releases idle waiters; not reproduced, so no dedicated test. `EachEntryHasItsOwnConversationWhichClosesWhenTheEntryFinishes` asserts no stage turn was sent as a steer | `src/Weavie.AgentClientProtocol/AcpConversation.Turns.cs:156` | `WhenIdleAsync` waiters are not released when a steer ends. `IdleLocked` reads `_steering`, but only prompt-delivery end and `SignalSettled` release waiters. A steer whose reply lands after its turn's prompt returned leaves the next stage's turn parked with nothing shown to the user. Not reproduced; the ordering depends on the agent. |
| W5 | fixed: the code keeps every declared output present with the entries that reported it (none for an empty list), on both paths through one `Repeated` builder; design.md now says so, since an absent value would pause every stage after an empty repetition | `src/Weavie.Workflows/WorkflowTransitions.Tree.cs:18` | An empty list stores `Entries = []` for every output; the design says `F.Outputs[S] = {}` and "with no entry reporting it the value is absent", so a later read pauses. `AnEmptyListSkipsTheStageWithEmptyOutputs` pins the code's behaviour, and `EndEntry` omits outputs no entry reported, so the two paths disagree. The design or the code changes. |
| W6 | fixed: only duplicate ids stop the tree checks; `ReaderAndTreeErrorsAreReportedTogether` | `src/Weavie.Workflows/WorkflowSemantics.cs:22` | Semantic checks stop at the first reader error. `check.HasErrors` includes the reader's template and output-type errors, so `outputs: { n: number }` with `then: nowhere` reports only the first. `main` reported both. |
| W7 | fixed: `ReportText` derives its wording from the stage (six parameters); `CheckQuestion` derives `seesOutputs` from the field (six) | `src/Weavie.Workflows/WorkflowTransitions.Tree.cs:108`, `src/Weavie.Workflows/WorkflowSemantics.cs:145` | `ReportText` is new with seven parameters and `CheckQuestion` grew from five to seven. |
| W8 | fixed in part: added engine tests (resume before a stage began, new record ids on a second pass, a fork leaving its source's owed reports, a round trip of entries, owed, opened, streaks and lost, an old-format run rejected), Core tests (`ATerminalAgentRefusesAForkNestedInAStageWithStages`, `SummaryNamesTheStagePathInsideRepeatedStages`), host tests (a refused nested fork with a new fake trigger file `refuse-nested-fork`, a fork with no agent running on its group's model, no steer between stages). The wrong question number is already covered (`LimitsCountPerEntryAndQuestionsReachMainWithTheirStage`). Rejected: a second pass after a host restart (record ids are proven distinct in the engine and kept conversations of gone records are forgotten by the existing path); a host-level answer from inside a group (`workflow-nested-stages.spec.ts` answers through the host); restore of a fork of a fork in the agent layer (restore is by owner key at any depth, and the real-agent probe restored nested forks on Claude and Codex) | `tests/` | Tests the design lists are missing. Hosting: the next stage's turn waits for the previous turn to end (the risk table calls it covered); a second pass after a host restart mid-entry; a fork with no agent runs on its source's model; a refused nested fork (`tools/Weavie.FakeAcp` has no refusal mode); a question from inside a group answered through the host. Agent layer: controls from the source; restore of a fork of a fork. Engine: an answer with the wrong question number; Resume before a stage began; new record ids on a second pass; a fork's opening turn leaving its source's owed reports (`WorkflowNestedTransitionsTests.cs:153` checks only the newer report); a round trip of `Entries`, `Owed`, `Opened`, `Streaks`, and `Lost`; an old-format run set aside. Core: `WorkflowAgentControls.Refusal` on nested stages; `WorkflowAdapterTests` checks `"b"` for a flat run, not a nested path. |
| W9 | fixed: `AgentOf` in the runner, `Address` for owed reports, `KeyOf` in the host, `Body`, `WorkflowRun.EntryCount`, `WorkflowValue.Label`, `WorkflowStage.Copied`, and one `Inside` message | `src/Weavie.Workflows`, `src/Weavie.Hosting` | Duplicated logic. The role-missing throw (`WorkflowRunner.cs:260`, `WorkflowRunner.Execution.cs:83`). Main-or-conversation owed addressing in `Report`, `OwedTo`, and `ClearOwed` (`WorkflowTransitions.Tree.cs:103`, `157`, `161`). `WorkflowConversationRef` to `AgentConversationKey` by hand (`HostSession.WorkflowConversation.cs:27`, `HostSession.Workflow.cs:90`, `137`). The body-record lookup (`WorkflowTransitions.Tree.cs:26`, `WorkflowTransitions.Values.cs:104`). The entry count lookup (`WorkflowRun.cs:142`, `WorkflowTransitions.Tree.cs:124`). The first line of an item (`WorkflowValues.cs:30`, `WorkflowTransitions.Tree.cs:124`). The copied-output source lookup (`WorkflowReader.cs:74`, `WorkflowSemantics.cs:59`). The "is inside stage" message (`WorkflowSemantics.cs:196`, `200`, with `OutermostBelow` called twice in one expression). |
| W10 | fixed: a record now tracks turns taken (`Prompted`) apart from conversations in use (`Opened`); the host continues any conversation in use, and a turn into a conversation that has taken none carries the owed reports of every unprompted fork it copies, outermost first; `AGroupOpenedToBeForkedStillTakesWhatIsOwedToItsSourceOnItsFirstTurn` | `src/Weavie.Hosting/HostSession.WorkflowConversation.cs:116`, `src/Weavie.Workflows/WorkflowTransitions.cs:218` | A group conversation opened for a child's fork never receives the reports owed to its source. The engine adds them only to a kickoff with `Continues` false; after `MarkOpened` the group's first own turn has `Continues` true, and the forking child's turn carries only what is owed to the group. With a fresh stage followed by a fork group whose first stage forks it, no turn in the group carries the fresh stage's report; before the B4 fix the group's own stage did. That first turn is also told it "continues the conversation of stage "group"; that work is above" when the group has taken no turn. Reproduced with a throwaway engine test. |

## Nits

| Id | Status | Where | Finding |
| --- | --- | --- | --- |
| N1 | fixed: `{{c.n}} is set inside stage "g"; only its outputs and then read it` | `src/Weavie.Workflows/WorkflowSemantics.cs:196` | Wrong diagnostic text: a stage's `if` reading its direct child reports `{{c.n}} is inside stage "c"; read an output of "c" instead`. |
| N2 | fixed: removed | `src/Weavie.Workflows/WorkflowSemantics.cs:5` | `Field.Outputs` is never passed. |
| N3 | fixed: a re-entered fork drops its earlier copy's owed reports | `src/Weavie.Workflows/WorkflowTransitions.Tree.cs:11` | A re-entered fork keeps `Owed[stage]`, so the new copy's first turn carries the previous copy's undelivered report. |
| N4 | open | `src/Weavie.Workflows/WorkflowTransitions.Tree.cs:64` | A stage whose stages were all skipped reports the summary of the stage before it as its own. |
| N5 | fixed with B2 | `src/Weavie.Workflows/WorkflowTransitions.Values.cs:85` | Missing is detected by record equality with `WorkflowValue.Empty`; `Lookup(...) is not { } value` says it directly. |
| N6 | open | `src/Weavie.Workflows/WorkflowTransitions.Tree.cs:149` | `Source` returns null both for main and for a record that is gone. |
| N7 | open | `src/Weavie.Workflows/WorkflowRunner.cs:193` | A stage with no context inside a group is told "The main conversation receives the result"; its result stays in the group's conversation. |
| N8 | fixed: the pause carries the error alone | `src/Weavie.Hosting/HostSession.WorkflowConversation.cs:136` | The pause reason reads "lost its conversation: its earlier one could not be restored: …"; the design passes the error alone. |
| N9 | fixed: the line names the conversation's path, or the reusing stage | `src/Weavie.Hosting/HostSession.WorkflowConversation.cs:151` | The line in main names a visiting child by bare stage id; the design's example is the path, `group 2 › task 3`. |
| N10 | fixed: comment shortened and the three states documented | `src/Weavie.Hosting/HostSession.WorkflowConversation.cs:122` | Three-line comment; `EnsureAsync` returns a three-state `bool?`. |
| N11 | open (reopened) | `src/Weavie.AgentClientProtocol/AcpConversation.Turns.cs:177`, `src/Weavie.Workflows/WorkflowRunner.cs` | Moving the idle waiters brought `AcpConversation.cs` to 312 lines but took `AcpConversation.Turns.cs` from 289 to 316, with a field declared mid-file, and `WorkflowRunner.cs` grew again, from 378 to 382. |
| N12 | open | `tests/Weavie.Workflows.Tests/TestWorkflows.cs:63` | The comment narrates history ("the names the run used before it held records"). |
| N13 | fixed | `tests/Weavie.Workflows.Tests/WorkflowNestedTransitionsTests.cs:150` | `Assert.Equal(["root"], run.Frames.Select(_ => "root"))` is `Assert.Single(run.Frames)`. |
| N14 | open | `src/web/e2e/harness/workflows.ts:64` | The doc comment says "The id of the workflow run"; the function returns the run. |
| N15 | open | `src/web/e2e/functional/workflow-nested-stages.spec.ts:38`, `src/web/e2e/functional/workflow-status-line.spec.ts:45` | `reply` and `complete` repeat `workflow-step-context.spec.ts:50`; `answerFor` nearly repeats the harness's `answerWorkflow`. |
| N16 | open | `docs/specs/workflows.md:657`, `docs/specs/workflows.md:726` | The examples still show `foreach: tasks.tasks` and `{{item}}`, which the reader rejects. |
| N17 | open | `docs/concepts/acp-conversations.md` | The design's file list names it; it is unchanged, and forking from any conversation is not described there. |
| N18 | rejected | `src/Weavie.Workflows/WorkflowRun.cs:168` | `WorkflowKickoff.Renews` is a conversation reference where the design has a bool. Rejected: a fork's source owner receives its child's kickoff (`SourceAsync`), so it must compare the renewed conversation to its own key (`HostSession.WorkflowConversation.cs:141`); a bool could not say which conversation is renewed. The design's type listing is what is out of date. |
| N19 | open | `src/Weavie.Workflows/WorkflowTransitions.Tree.cs:108` | With the W7 fix, a question in main from a fork or fresh stage inside a group opens with `ran in its own conversation and completed`; the design's sample for that turn says `completed`. The design sample or the wording changes. |

## Conformant

Checked against the design and found to match: the formats (stage block position, owed report with entry, question
turn in main, answer command, saved run, repeated-output rendering); the definition tree, values, and records table;
every diagnostics row except those in B1, B3, and N1; `Enter`, `StartEntry`, `Complete`, `Finish`, `EndEntry`,
`Advance`, streaks per record, Route, Resume with `Renews` and `Lost`, Jump, `ConversationLost`, and Cancel with no
definition, with every step re-reading its record by id; the runner's visit conversation and authority; owners by
key, delivery by `kickoff.Conversation`, the idle wait before a stage-opening turn and before a fork, and closing by
live records; `ForkAsync`, `HasHistory`, controls from the source; the summary path, command descriptions, and
authoring prompt. No optional parameters, nullable injected dependencies, suppressions, or reflection-based JSON
were added.
