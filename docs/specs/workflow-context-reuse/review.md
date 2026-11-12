# Workflow context reuse: review

Review of [PR #1200](https://github.com/Kapps/weavie/pull/1200) against [design.md](design.md), from the design and
the branch diff. The diff reviewed is commit `83117465`; the branch head is `318d1744`, a merge of `main` (#1198) that
adds nothing to the PR's own changes. Line numbers are at `318d1744`.

Sources: the `weavie-reviewer` agent, then a duplication pass over the whole diff. Every finding was checked against
the code. "Read" means confirmed by reading the cited lines; "run" means confirmed by running something. Nothing is
fixed in the review itself; see [Resolution](#resolution).

## Verdict

Four bugs and nine warnings are open. The engine half (reader, schema, diagnostics, `Opener`, the stage-block
sentence) conforms to the design. The defects are in the conversation owner's error and hand-over paths, in how a
kept conversation is forgotten, and in test coverage.

## Findings

| Id | Severity | Status | Where | Finding |
| --- | --- | --- | --- | --- |
| B1 | bug | fixed | `src/Weavie.Hosting/HostSession.Workflow.cs:115` | Forgetting runs under the runner lock and takes the agent's lock, the reverse of prompt delivery: a deadlock |
| B2 | bug | fixed | `src/Weavie.Hosting/HostSession.WorkflowConversation.cs:127` | When applying the stage's agent fails, the new conversation stays open and Resume runs the stage on main's model |
| B3 | bug | fixed | `src/Weavie.Hosting/HostSession.Workflow.cs:125` | Unloading a session pauses a run that is only waiting on a fork or fresh stage's `if` question |
| B4 | bug | fixed | `src/Weavie.Hosting/HostSession.WorkflowConversation.cs:68` | A Jump can skip the interrupt, so the old turn keeps running and can complete the new visit |
| W1 | warn | fixed | `src/Weavie.AgentClientProtocol/AcpAgentSession.Conversations.cs:84` | Unloading while a restore is in flight forgets the kept conversation |
| W2 | warn | fixed | `src/Weavie.Hosting/HostSession.WorkflowConversation.cs:137` | One MCP grant leaks each time a conversation opens after a restore that found nothing |
| W3 | warn | rejected | `src/Weavie.Hosting/HostSession.WorkflowConversation.cs:70` | Resume of a `fork` stage after a reload forks main again instead of restoring |
| W4 | warn | fixed | `src/Weavie.AgentClientProtocol/AcpAgentSession.Rewind.cs:26` | Rewind does not end a kept conversation that is not live |
| W5 | warn | fixed | `src/Weavie.AgentClientProtocol/AcpAgentSession.Conversations.cs:145` | Restoring on a provider with neither `session/load` nor `session/resume` fails the whole agent |
| W6 | warn | fixed | `src/Weavie.AgentClientProtocol/AcpAgentSession.Conversations.cs:74` | The fork-support check no longer runs when main has no turns |
| W7 | warn | fixed | `tests/Weavie.Hosting.Tests/AcpOwnedConversationTests.cs:211` | The tests for "only the owner restores" pass with the guard removed |
| W8 | warn | fixed | `tests/` | Eleven tests the design lists are missing |
| W9 | warn | rejected | CI | `.NET tests` failed on `83117465`, so the hosting tests never ran in CI |
| N1 | nit | open | `src/Weavie.Workflows/WorkflowRunner.cs:93` | Pause and diagnostic texts differ from the design's |
| N2 | nit | fixed | `src/Weavie.Hosting/HostSession.WorkflowConversation.cs:121` | A `fork` whose second fork failed posts "its earlier one ended" on Resume |
| N3 | nit | open | `src/Weavie.AgentClientProtocol/AcpAgentSession.Conversations.cs:45` | A store failure while forgetting skips the archive-failure path |
| N4 | nit | open | `src/Weavie.AgentClientProtocol/AcpConversationPort.cs:98` | `Closed`/`OnClosed` is a hook nothing overrides any more |
| N5 | nit | fixed | `src/Weavie.Hosting/HostSession.Workflow.cs:88` | "Owners belong to another run" is handled in two places; one looks unreachable |
| N6 | nit | fixed | `src/Weavie.Hosting/HostSession.WorkflowConversation.cs:158` | The owner disposes its grant and conversation in two places, and repeats its cancellation scaffold three times |
| N7 | nit | open | `src/Weavie.AgentClientProtocol/AcpAgentSession.Conversations.cs:136` | `IndependentPort.Store` repeats `SidePort.Store` |
| N8 | nit | open | `src/Weavie.Workflows/WorkflowReader.cs:65` | Stage lookup by id is written three more times, and the valid-reuse rule twice |
| N9 | nit | fixed | `tests/Weavie.Hosting.Tests/HostCoreWorkflowConversationTests.Reuse.cs:37` | Repeated test scaffolding |
| R1 | bug | rejected | `src/Weavie.Hosting/HostSession.Workflow.cs:87` | Lock inversion between the owner dictionary and the runner |
| R2 | bug | rejected | `src/Weavie.Hosting/HostSession.WorkflowConversation.cs:105` | Closing a suspended conversation before restoring deletes its record |
| R3 | nit | rejected | `src/Weavie.Hosting/HostSession.WorkflowConversation.cs:17` | The owner never disposes `_lifetime` |
| R4 | warn | rejected | `src/Weavie.Core/Sessions/AcpConversationState.cs:22` | The new `Owner` record breaks Native AOT JSON |

## Resolution

Fixed on the branch after `318d1744`. Each fix has a test unless the entry says why not.

| Id | Resolution |
| --- | --- |
| B1 | The forget sweep yields before taking the agent's lock, so it never runs on the runner's thread. No test: the deadlock needs a prompt delivery racing a run's end, which no deterministic test controls. |
| B2 | A conversation whose agent can't be applied is closed before the refusal pauses the run. The "opened again" sentence now depends on whether the conversation has taken a stage turn, not on whether it was just opened, so a refusal after opening no longer leaves it wrong. Test: `AStageWhoseAgentCannotApplyClosesItsConversationAndSendsNothing`. |
| B3 | Unload pauses only a visit that borrows the conversation (`WorkflowConversationOwner.Lends`), as the old owner check did. Test: `UnloadingWhileAStageAsksWhetherToRunLeavesTheQuestionOpen`. |
| B4 | The hand-over releases the previous visit itself, interrupting a turn a jump or cancel left running, whichever of it and `ReturnAsync` runs first. Interrupting holds the new turn back until the cancelled one ends. Test: `AJumpToTheOpenStageStopsItsTurnBeforeTheNewVisitTakesTheConversation`, which fails 3 of 3 runs without the fix. |
| W1 | A cancelled restore suspends the conversation instead of closing it, so the record is kept. The test exposed a second defect: restoring that session again in the same agent process failed the whole agent ("already has an owner"). `RestoreAsync` now refuses a session the running process already held (`AcpRuntime.Bound`), and the owner opens a new conversation with the transcript line. Test: `ACancelledRestoreKeepsTheConversationForTheNextProcess` (both cases). |
| W2 | `RestoreAsync` disposes the binding's grant when it returns nothing. No test: grants are not observable outside the MCP server. |
| W3 | Rejected. Telling a resumed fork visit from a new one after a reload needs a visit identity persisted with the run, which the design keeps out of the run file. The behaviour matches what Resume did before this change, and the case is a reload in the middle of a fork stage. `docs/specs/workflows.md` now says so. |
| W4 | Rewind forgets every kept conversation, live or not. Test: `ClearAndRewindForgetKeptConversationsWhetherLiveOrNot`, which also covers `/clear`. |
| W5 | `RestoreAsync` forgets the record and throws when the agent advertises neither restore method, and the owner opens a new conversation. No test: the fake agent always advertises one. |
| W6 | The fork-support check moved into `NewChild` and runs for every fork, as before. No test: it restores the prior check. |
| W7 | The test waits for main to be ready and asserts the guard's own message. |
| W8 | Added: a skill stage's sentence, `/clear` and Rewind forgetting, cancel closing, Jump interrupting, a reply between visits in the e2e spec, and tests for B3 and W1. Covered already: a restored session's new credential, because the restart and reload tests complete the stage from the restored conversation; a row without `owner`, which every existing store round trip saves. Rejected: main's permission mode on a later visit, because the fake agent keeps one mode for all its sessions; a crash leaving the conversation unrestorable, which is the unchanged aside path. `RewindClosesAnIdleStageAndPausesIt` still holds: Rewind closes a lent stage and pauses it. |
| W9 | Rejected for this PR. The PTY test does not touch this change, `.NET tests` passed on `318d1744`, and by the user's decision the flake gets its own root-cause PR after this one merges. |
| N2 | A fork that is forking again is tracked across the failed attempt, so Resume posts no line. |
| N5 | Removed the unreachable end-of-run check in `ConversationOwner`. |
| N6 | The owner disposes its grant and conversation in one place (`CloseAsync`). The linked-token scaffold stays: each of its three loops waits on a different event. |
| N9 | `JsonElementThread` is gone and `Fixed` is now `FixedAsync`. The other repeated scaffolding is left open. |

## Bugs

### B1. Forgetting runs under the runner lock

`ForgetWorkflowConversations` wraps a lambda with no `await` in `Background.Run`. `SessionTaskScope.Run`
(`src/Weavie.Hosting/SessionTaskScope.cs:22`, `:44`) completes a plain `TaskCompletionSource`, so the lambda runs on
the caller's thread. Both callers hold the runner lock: the `Changed` handler is raised inside `WorkflowRunner.Commit`,
and `ConversationOwner` runs inside `Deliver`. `Forget` then takes the agent's transition gate and deletes from SQLite.

Prompt delivery takes the locks the other way: `AcpConversation.PromptDelivery.cs:17-20` holds the transition gate and
calls `BuildPrompt`, whose `TurnContext.Compose()` takes the runner lock.

A run that finishes or is cancelled while any conversation is delivering a prompt can hang the session. The comment
on line 114 says the forget "runs apart"; it does not. `SendAsync` and `RunLifetimeAsync` start with
`await Task.Yield()` for this reason.

Confirmed by read. The deadlock itself was not reproduced.

### B2. A failed agent apply leaves the conversation open

`EnsureAsync` assigns `_conversation`, then calls `ApplyAgentAsync`, then starts `ObserveAsync`. If the model or an
option is refused, the exception reaches `RefuseOpen` and the run pauses with the conversation still live.

On Resume the visit is the same and `Live` is not null, so `EnsureAsync` is skipped. The stage's prompt is sent on
main's model, with "has opened again; your earlier work on it is above" over an empty conversation. Nothing observes
that conversation, so its ending no longer pauses the visit.

The deleted `WorkflowStageOwner.OpenAsync` disposed the conversation and its grant on any failure. The design's error
table says "Nothing is substituted". The same wrong sentence appears after any refusal that follows a successful open,
such as a missing prompt image.

Confirmed by read.

### B3. Unload pauses a run waiting on an `if` question

`DisposeWorkflow` now pauses whenever the current visit has an opener and the run owes no turn. A fork or fresh stage
with a user `if` is in that state once its question has been delivered to main, though no stage conversation exists.

The run reloads as Paused with "lost its conversation" instead of still asking. `WorkflowRunner.Activate`
(`src/Weavie.Workflows/WorkflowRunner.cs:90-92`) excludes this case with `run.Decision != WorkflowDecisionKind.If`;
the old condition excluded it by requiring an owner for the visit.

Confirmed by read.

### B4. A Jump can skip the interrupt

A Jump retires the old visit and delivers the new one in the same `Commit`. `ReturnAsync` for the old visit and
`SendAsync` for the new one then race for `_delivery`. If `SendAsync` wins, it sets `_lease.Visit` to the new visit
and submits; `ReturnAsync` sees a different visit at line 172 and returns before `Live?.Interrupt()`.

The old turn keeps running, and a `completeStage` it makes captures the new visit. With a reusing stage that completes
`recheck` with `review`'s work. The hand-over at line 68 never looks at the outgoing visit.

Confirmed by read that nothing orders the two; which side usually wins was not measured, and the design's test "Jump
to the open stage interrupts and re-prompts" is absent (W8).

## Warnings

### W1. A cancelled restore forgets the conversation

`OpenOwnedAsync`'s `catch` disposes the handle on any exception, cancellation included. Dispose is
`AcpRuntime.CloseAsync`, which now calls `conversation.Forget()` (`src/Weavie.AgentClientProtocol/AcpRuntime.cs:134`).
Unload cancels background work before it disposes the agent (`src/Weavie.Hosting/HostSession.Lifetime.cs`), so the
record is deleted and its card closed. The design says owners stop on unload without forgetting. Confirmed by read.

### W2. A grant leaks per open

`Binding()` creates a grant and stores it in `_tools` on every call. `RestoreAsync` returns null at
`src/Weavie.AgentClientProtocol/AcpAgentSession.Conversations.cs:25` without disposing `binding.Tools`, and
`EnsureAsync` then calls `Binding()` again. The first grant stays in `McpServer._grants` until the registry is
disposed. This happens on every stage's first open and every replacement. Confirmed by read.

### W3. Resume of a fork after a reload forks again

After a reload `Activate` creates a new visit object and the owner is new, so the hand-over branch runs. For a `fork`
opener on its own stage it sets `reopened`, which skips the restore; `NewChild` then forgets the kept fork. The design
says Resume is "the same visit, so no hand-over and no second fork", and `docs/specs/workflows.md` says Resume restores
after a reload. Restart Agent in the same process does restore. Confirmed by read; the unload test covers `fresh` only.

### W4. Rewind keeps a conversation that is not live

Line 26 closes only `_runtime.Conversations`. `CommitRewind` (lines 66-67) writes back every kept record anchored at
or before the rewind point, owned ones included. After Restart Agent between visits, a Rewind leaves the record and
the next visit restores it. The design says "any Rewind ends them". Confirmed by read.

### W5. A provider that cannot restore loses the whole agent

`RestoreAsync` does not check `Features.Load || Features.Resume`. `OpenSessionAsync`
(`src/Weavie.AgentClientProtocol/AcpConversation.Opening.cs:26-29`) throws `AcpProtocolException`, and
`IndependentPort.OnFail` sends that type to `Runtime.Fail`, which ends main too. The design specifies a forgotten
record, a new conversation, a transcript line, and no pause. Confirmed by read. No agent Weavie launches today lacks
both methods.

### W6. Fork support is not checked at zero turns

The check was `if (fork)`; it is now `if (opening.Opening is ForkFromOpening)`, and `ChildSpec` produces that only
when main has a turn. A `fork` stage that runs first on an agent that cannot fork opens a plain session, then is
refused on its second visit. The design does not ask for this. Confirmed by read.

### W7. The owner guard is untested

With `{ Owner: null }` at `src/Weavie.AgentClientProtocol/AcpAgentSession.SideConversations.cs:47` changed to `{ }`,
`AKeptConversationIsRestoredOnlyByItsOwnerAfterARestartAndForgottenWhenClosed` and
`ProcessRestartPausesTheStageAndResumeContinuesItsConversation` both still pass. Their `Assert.Throws` runs straight
after the restart, when `EnsureSideConversationSupport` throws the same exception type because main is not ready.
Confirmed by run; the change was reverted.

### W8. Tests the design lists are missing

- Engine: the sentence for a skill stage (`WorkflowStageExecutionTests.cs` is untouched).
- Conversation layer: a crash leaves the conversation unrestorable; `/clear` forgets it; Rewind forgets it; a restored
  session carries a new credential; a row without `owner` reads; a refused restore throws and forgets (covered only
  through hosting).
- Hosting: Jump to the open stage interrupts and re-prompts; mode follows main on the second visit; cancel closes.
  `RewindClosesAnIdleStageAndPausesIt` was to be rewritten and is unchanged.
- Web: the e2e spec has a return visit and no reply between visits.

B3, W1, and W3 have no test either. Confirmed by read.

### W9. CI is red on the reviewed commit

`Test Core` failed on `83117465`: `PosixPtyTerminalTests.Dispose_HangsUpAnInteractiveShell_InsteadOfKillingIt`
expected exit code 129 and got 137. The diff does not touch that code, and `Weavie.Core.Tests` passes locally (1799
of 1799), so it is a flake to root-cause, not to rerun past. The failure skipped `Test Hosting`; the two changed
hosting test classes pass locally (38 of 38). A new run on `318d1744` was pending when this was written.

## Nits

- **N1.** The three pause reasons end "Resume to continue." (`WorkflowRunner.cs:93`, `HostSession.Workflow.cs:126`,
  `HostSession.WorkflowConversation.cs:187`); the design says "Resume to continue it." `WorkflowSemantics.cs:91` says
  `set it on "x"` where the design says `set agent on "x"`, and also reports at `.run.role`, which the design's table
  omits.
- **N2.** On a `fork` stage's second visit, if `OpenForkAsync` throws and the user resumes, `reopened` is false and
  `_held` is true, so the line is posted. The design posts none for a fork forking again.
- **N3.** `ForgetKept` calls `_sessions.Forget` with no handler, where saves go through `OnSave` and `ArchiveFailed`
  (`AcpConversationPort.cs:142`). In `AcpRuntime.CloseAsync` the throw would land before `Retire()`. The structure is
  confirmed by read; a failing store was not exercised.
- **N4.** The PR removed the only `OnClosed` override. `AcpConversationPort.Closed`, `OnClosed`, and the call at
  `AcpConversation.Lifecycle.cs:134` now do nothing, beside the new `Forget`/`OnForget` pair.
- **N5.** `ConversationOwner` (line 88) and the `Changed` handler (lines 99-106) both end owners of another run. A
  run starts only after the previous one finished, and that finish already raised `Changed`, so line 88 appears
  unreachable.
- **N6.** `RunLifetimeAsync` (lines 41-46) and `CloseAsync` (lines 158-163) both dispose the grant, then the
  conversation. The linked token source with its `catch … when (stopping.IsCancellationRequested)` appears at lines
  62, 167, and 183.
- **N7.** The keep branch at `AcpAgentSession.Conversations.cs:136-137` is `SidePort.Store`
  (`AcpNestedPorts.cs:52-53`) line for line.
- **N8.** `Stages.First(s => s.Id == …)` is added at `WorkflowDefinition.cs:50`, `WorkflowReader.cs:66`, and
  `WorkflowSemantics.cs:81`, beside five existing copies. `WorkflowReader.Resolve` restates "the named stage is not
  itself reusing and is not `continue`", which `CheckReuse` reports.
- **N9.** The fake agent's transcript path is built at `HostCoreWorkflowConversationTests.cs:21`,
  `.Lifecycle.cs:45`, and `.Reuse.cs:37`. `JsonElementThread` (`.Reuse.cs:39`) wraps a string and holds no
  `JsonElement`. `Fixed` (`.Reuse.cs:29`) is async without the suffix. Four `Reuse` tests open with the same four
  lines, and `fixture.Send(child, Completion)` followed by the wait for stage `fix` appears five times. In
  `src/web/e2e/functional/workflow-step-context.spec.ts` the `weavie.workflow.answer` call is written four times.

## Rejected

- **R1.** `DeliverWorkflowTurn` and the `Changed` handler are both called from inside `WorkflowRunner.Commit`, so the
  order is always runner lock, then dictionary.
- **R2.** `Retire` detaches the port (`AcpConversation.Lifecycle.cs:134-136`), so the later `Forget` does nothing.
  `AnAgentRestartBetweenVisitsLeavesTheRunOpen…` covers it and passes.
- **R3.** The source holds no timer or wait handle, and every linked source made from it is disposed.
- **R4.** `native build (linux)` passed, and the store round trip in `AcpOwnedConversationTests` reads `Owner` back.
