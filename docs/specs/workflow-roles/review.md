# Workflow roles — review

Review of PR #1194 against [design.md](design.md). Each pass is a fresh-context review by the repo's reviewer agent plus
a duplication pass over the diff, and every finding was checked against the code.

- **First pass**: commit `106e355d`, the whole feature.
- **Second pass**: commit `cef27991`, the fixes; it confirms each fix and reviews only what that commit changed.
- Second-pass findings are fixed in the commit after `cef27991`.

## Findings

| # | Severity | Status | Finding |
| --- | --- | --- | --- |
| 1 | bug | fixed | A pick can save a role name the role file's own loader rejects, leaving the file unusable. |
| 2 | warn | fixed | Several picks are saved one at a time, so a refused start can leave some saved. |
| 3 | warn | fixed | The `setRole` command description names a tool that doesn't exist. |
| 4 | warn | fixed | A failed model list never recovers for the shown workflow and provider. |
| 5 | warn | fixed | Resolving reads the model catalog up to three times; a refresh in between crashes instead of refusing. |
| 6 | nit | fixed | A missing `agent.provider` is reported as `'provider' is required.`, the argument the caller did give. |
| 7 | nit | fixed | A role suggestion re-read every workflow through its own copy of the catalog's winner lookup. |
| 8 | nit | fixed | Tests the design's strategy lists are absent. |
| 9 | nit | fixed | The error-message helper is copied between the two composer state files. |
| 10 | nit | rejected | Three near-identical "fills no role" test doubles. |
| 11 | nit | fixed | Three wordings for an unregistered provider. |
| 12 | nit | rejected | `WorkflowRoleAgent` repeats `WorkflowAgent`'s provider, model, and options. |
| 13 | warn | fixed | "No pick is saved for a refused start" rested on the order of steps in `Prepare`, with no test. |
| 14 | nit | fixed | A start with picks reported "models aren't known yet" where the provider can't run the workflow at all. |
| 15 | nit | fixed | The fixes copied two pieces of logic: the required-argument check and the role-name rule. |
| 16 | nit | fixed | The `listWorkflows` roles assertions sat in the test named for forking. |
| 17 | nit | fixed | The design's error table lacked the refusals the fixes added. |

## Detail

### 1. A pick can save a role name the loader rejects (bug)

`WorkflowRoleResolver.Pick` (`src/Weavie.Core/Workflows/WorkflowRoleResolver.cs:57`) checks only the model, and
`WorkflowRoleStore.Set` (`src/Weavie.Core/Workflows/WorkflowRoleStore.cs:54`) only that the role is non-empty, while
`Read` (`:86`) rejects any name failing `WorkflowSchema.IsRoleName` and holds the whole file as unusable. Only
`WorkflowRoleCommand` validates the name. `weavie.session.new` with `roles: {"Planner": "opus"}` saves the entry
(picks are saved before `Prepare`, so the workflow need not name the role); after the next restart every read and
write throws `Workflow roles could not be loaded…`, including the `clear` that would repair it, the composer's
`workflows` request, and `listWorkflows`. Recovery is hand-editing `~/.weavie/workflow-roles.json`.

The store should enforce on `Set` what `Read` requires, and a pick should be refused for a role the workflow doesn't
name.

**Fixed.** `WorkflowRoleStore.Set` checks every entry with the same rule `Read` applies (one `Problem` helper), so nothing is saved the file can't be read back with, and a start's picks are refused for a role the workflow doesn't ask for. Tests: `TheStoreRefusesWhatItCouldNotReadBack`, `PicksFillRolesAndAreSavedTogetherOnlyWhenTheWholeStartResolves`, and the `stray` start in `ANewSessionSavesItsPicksBeforeItStartsAndABadPickLeavesNothing`.

### 2. Picks are saved one at a time (warn)

`src/Weavie.Hosting/HostCore.Sessions.cs:707` runs `roles.Pick` per entry. With `{planner: "fable", builder: "typo"}`,
`planner` is written (its previous options dropped), `builder` throws, and the start is refused. The design's error
table says a bad pick saves nothing. `ANewSessionSavesItsPicksBeforeItStartsAndABadPickLeavesNothing`
(`tests/Weavie.Hosting.Tests/HostCoreWorkflowRoleTests.cs:70`) sends one pick and never reads the store, so it doesn't
prove its name.

**Fixed.** Picks go into the start as `WorkflowRoleResolver.Picking(picks)`: resolving checks them all, fills the roles from them, and saves them in one write only when nothing refused. `WorkflowCatalog.Prepare` resolves roles last, so a start refused for its inputs or prompt saves no pick. The hosting test now sends a mixed good-and-bad set and asserts the store stays empty.

### 3. The command description names a missing tool (warn)

`src/Weavie.Core/Commands/CoreCommands.Workflows.cs:38` says "listWorkflowRoles shows what is saved". No such tool
exists; saved roles come from `listWorkflows`. An agent following the description calls a tool that isn't there.

**Fixed.** The description points at `listWorkflows`.

### 4. A failed model list never recovers (warn)

`NewSessionRoleChoice.show` (`src/web/src/chrome/new-session-role-choice.ts:62`) returns early when the target,
workflow, and roles are unchanged, and nothing else asks for the models again. After a failed list (a failed probe, or
the request failing across a reconnect) the rows show `Couldn't list models: …` and Start stays disabled; fixing the
cause and reopening Sessions re-reads the catalog but produces the same key. Only switching workflow or provider away
and back recovers. No unit test covers recovery.

**Fixed.** `show` lists the models again for the same pair while its last list failed, so reopening Sessions retries. Test: `reports a failed model list, asks nothing, and lists again when shown again`.

### 5. One resolution reads the model catalog several times (warn)

`WorkflowRoleResolver.Read` (`src/Weavie.Core/Workflows/WorkflowRoleResolver.cs:28`) reads `models.Find` and then
`Offered`, and `Resolve` (`:52`) reads `Offered` again for `WorkflowAgentControls.Unfilled`, which dereferences
`offered!` (`src/Weavie.Core/Workflows/WorkflowAgentControls.cs:63`). A stale-model gap exists only when the list was
known; if the provider catalog refreshes between the reads the entry is back to probing and the refusal throws
`NullReferenceException`, which the `WorkflowException` catches in `NewSessionAsync` and `RunWorkflowCommand` don't
handle. The window is small. The resolution should carry the one snapshot it decided from.

**Fixed.** `Read` takes one snapshot of the provider's models and carries it as `WorkflowRoleResolution.Offered`; `Resolve`, the pick checks, and the role suggestion all decide from it, and the separate `Offered` property is gone.

### 6. The wrong argument is named (nit)

`src/Weavie.Core/Workflows/WorkflowRoleCommand.cs:32` reads `agent.provider` through the shared required-argument
check, so `{"role":"planner","provider":"claude-acp","agent":{"model":"fable"}}` fails with `'provider' is required.`.
The design's wording is `'agent' needs 'provider' and 'model'.`; `tests/Weavie.Core.Tests/Workflows/WorkflowRolesTests.cs:135`
pins the ambiguous text.

**Fixed.** A missing or malformed `agent.provider` or `agent.model` reports `'agent' needs 'provider' and 'model'.`; the test pins that text.

### 7. A role suggestion redoes the whole catalog (nit)

`SuggestRoleAsync` (`src/Weavie.Hosting/HostCore.WorkflowStart.cs:81`) calls `WorkflowStartOptionsAsync`, which lists
every workflow file and resolves every workflow's roles, to find one workflow's one gap, and builds a second resolver
for the model list. The composer sends one request per unfilled role, so choosing a workflow with two roles reads the
catalog three times.

**Fixed.** `SuggestRoleAsync` reads the one workflow it is asked about and resolves only its roles, through one resolver.

### 8. Missing tests (nit)

- No Core test covers `roles` in `listWorkflows` (per workflow or the saved list); one e2e regex in
  `src/web/e2e/functional/workflow-agent.spec.ts` is the only coverage.
- No hosting test covers a run's roles surviving unload and reload; only the engine's runner test does.
- No web unit test changes `providerId`: `src/web/src/chrome/new-session-workflow-choice.test.ts` uses one constant
  provider, so the new comparison in `sameTarget` is untested there.
- Nothing tests `'roles' needs a 'workflow'.`

**Fixed.** `listWorkflows` roles (per workflow and saved) are asserted in `RegistryMcpTests`; the hosting role test unloads and reloads the session and checks the run kept its role; the web workflow-choice tests re-read for another provider and drop a workflow it can't run; `'roles' needs a 'workflow'.` is asserted in the hosting test.

### 9. Copied helper (nit)

`const message = (error: unknown) => …` at `src/web/src/chrome/new-session-role-choice.ts:36` is the helper at
`src/web/src/chrome/new-session-workflow-choice.ts:38`, in a module the new file already imports from.

**Fixed.** `errorMessage` is exported from `new-session-workflow-choice.ts` and imported by the role choice.

### 10. Test doubles (nit)

`NoWorkflowRoles` is defined identically in `tests/Weavie.Core.Tests/Workflows/WorkflowRolesTests.cs:12` and
`tests/Weavie.Hosting.Tests/AcpAgentSessionFixture.cs:726`, beside `FixedRoles([])` in
`tests/Weavie.Workflows.Tests/TestWorkflows.cs:27`. The projects share only `Weavie.TestSupport`, which references
neither Core nor the engine.

**Rejected.** The three test projects share only `Weavie.TestSupport`, which references neither Core nor the engine. Sharing a four-line double would mean adding those references to the shared support project, more than the copies it removes.

### 11. Unregistered provider wording (nit)

`RequireProvider` (`src/Weavie.Hosting/HostCore.WorkflowStart.cs:28`) repeats the text of
`AgentProviderRegistry.RequireAvailable` (`src/Weavie.Core/Agents/AgentProviderRegistry.cs:56`), and
`WorkflowRoleCommand.Registered` (`src/Weavie.Core/Workflows/WorkflowRoleCommand.cs:39`) words the same failure a third
way.

**Fixed.** `WeavieWorkflows.RequireProvider` is the one check, naming the registered providers; the composer requests and the role command both use it.

### 12. A second agent record (rejected)

`WorkflowRoleAgent` holds the same three values as the engine's `WorkflowAgent`. Rejected: the stored form requires a
model and has no name (the name is the file's key), while `WorkflowAgent` is named and its model is optional; storing
the engine type would put a redundant name in the file and admit a role with no model.

## Second pass

### Fixes confirmed

- **1** holds: `WorkflowRoleStore.Set` and `Read` share one `Problem` rule (`src/Weavie.Core/Workflows/WorkflowRoleStore.cs:95`),
  and a pick for a role the workflow doesn't ask for is refused (`src/Weavie.Core/Workflows/WorkflowRoleResolver.cs:62`).
- **2** holds for partial saves: every pick check runs before one `roles.Set` (`src/Weavie.Core/Workflows/WorkflowRoleResolver.cs:69`).
  Refusals after `Prepare` (an unavailable provider, a bad branch, worktree creation) still leave the picks saved, which
  is the design's accepted open question 3. See 13 for what guards the rest.
- **3**, **5**, **6**, **9** hold as described under each finding.
- **4** holds on the client: the field's effect calls `show` on every workflow-state publish
  (`src/web/src/mobile/NewSessionWorkflowField.tsx:99`), so reopening Sessions lists again, with no retry loop. The
  host answers from the catalog's held failure, so the list changes only once the provider's models do.
- **8** holds: all four tests exist and would fail without the behaviour (the reload test changes the saved role first).
  See 16 for where one of them sits.

### 7. Reopened: the suggestion still lists every workflow (nit)

`SuggestRoleAsync` (`src/Weavie.Hosting/HostCore.WorkflowStart.cs:83`) no longer resolves every workflow's roles and
uses one resolver, but it calls `WeavieWorkflows.Catalog(root).List()`, which reads and parses every built-in,
personal, and repository workflow file, once per role row. Its `FirstOrDefault(entry => entry.Id == … && entry.ShadowedBy is null)`
repeats the catalog's private `Resolve` (`src/Weavie.Workflows/WorkflowCatalog.cs:89`), as
`src/Weavie.Core/Workflows/WorkflowStartOptions.cs:36` already does. The catalog offers no public single-workflow
lookup; the fix commit's "reads only its own workflow" overstates what changed.

**Fixed.** `WorkflowCatalog.Winner(id)` is now the one public winner lookup, used by the catalog's own `Prepare` and by `SuggestRoleAsync`. Reading every scope's files to find the winner is how the catalog resolves any id, a start included; a suggestion is asked once per unfilled role, so its cost was left as it is.

### 11. Reopened: provider wording (nit)

`WeavieWorkflows.RequireProvider` (`src/Weavie.Core/Workflows/WeavieWorkflows.cs:10`) replaced the feature's two
lookups, but it words the failure differently from `AgentProviderRegistry.RequireAvailable`
(`src/Weavie.Core/Agents/AgentProviderRegistry.cs:56`). `weavie.session.new` with an unknown `agentProviderId` now
answers "No agent provider 'x' is registered; the providers are …" with a `workflow`
(`src/Weavie.Hosting/HostCore.Sessions.cs:709`) and "Agent provider 'x' is not registered." without one (`:1127`).

**Fixed.** `AgentProviderRegistry.Unregistered(id)` holds the one wording, naming the registered providers (or none). `RequireAvailable` and `WeavieWorkflows.RequireProvider` both use it, so `weavie.session.new` answers the same with or without a `workflow`. The consult tool keeps its own text, which points the agent at `listAgents`. `InferenceModelDescriptionTests` pins the new text.

### 13. The guarantee is held by ordering and a comment (warn)

`Picked.Resolve` writes the role file as a side effect of the engine's resolver seam
(`src/Weavie.Core/Workflows/WorkflowRoleResolver.cs:69`), so "nothing is saved for a refused start" is true only while
`roles.Resolve` stays the last step of `WorkflowCatalog.Prepare` that can refuse (`src/Weavie.Workflows/WorkflowCatalog.cs:81`).
A comment says so; `IWorkflowRoleResolver.Resolve`'s doc still describes a lookup. No test fails if the order is
reversed: `PicksFillRolesAndAreSavedTogetherOnlyWhenTheWholeStartResolves`
(`tests/Weavie.Core.Tests/Workflows/WorkflowRolesTests.cs:126`) calls the resolver directly and covers only the pick
checks, and the hosting test's workflow has no inputs or prompt. With `ReadInputs` moved back after `Resolve`,
`weavie.session.new { workflow, roles: { builder: "GPT Next" }, inputs: { bogus: 1 } }` would save `builder` and fail
on the input. A valid pick refused because another role is unfilled is also untested.

Saving the picks after `Prepare` returns, or a test through `Prepare` with a refused input, would hold it.

**Fixed.** Resolving picks no longer writes anything. `Picking(picks)` returns `WorkflowRoleResolver.Picks`, whose `Resolve` fills roles from the picks and whose `Save` writes them in one call, refusing until a resolve has succeeded. `NewSessionAsync` saves after `Prepare` returns, so no ordering inside `Prepare` matters, and the ordering comment is gone. Tests: `PicksAreSavedOnlyOnceTheirLaunchWasPrepared`; a valid pick refused because another role is unfilled; and a `weavie.session.new` with a valid pick and an unknown input that leaves the store empty.

### 14. Pick checks mask the real refusal (nit)

`Resolve` (`src/Weavie.Core/Workflows/WorkflowRoleResolver.cs:62`) checks the picks before `read.Refusal`.
`weavie.session.new` with `agentProviderId: "claude"`, `workflow: "spec-driven"`, and a pick answers "Claude Code's
models aren't known yet, so its roles can't be picked." instead of the "runs in a terminal" refusal; a failed probe is
reported the same way without its error, and a provider with no model choice yields "offers ." with an empty list
instead of `NoModelChoice`. Only a direct command reaches it: the composer shows no pickers in these states.

**Fixed.** The provider's own refusal is checked before the picks, so terminal Claude says it runs in a terminal and a provider with no model choice gets `NoModelChoice`. Test: `APickNeverMasksWhyTheProviderCantRunTheWorkflow`.

### 15. Logic copied by the fixes (nit)

- `WorkflowRoleCommand.AgentArgument` (`src/Weavie.Core/Workflows/WorkflowRoleCommand.cs:40`) is
  `WeavieWorkflows.Required` (`src/Weavie.Core/Workflows/WeavieWorkflows.cs:17`) with another message.
- The role-name check and its sentence are in both `src/Weavie.Core/Workflows/WorkflowRoleCommand.cs:16` and
  `src/Weavie.Core/Workflows/WorkflowRoleStore.cs:97`; the store's now covers the command's write.

**Fixed.** `WeavieWorkflows.Required` takes the path it reports (`'agent.provider' is required.`) and replaces `AgentArgument`. The command no longer checks role names: the store's rule refuses the write, and a clear of a malformed name finds nothing saved.

### 16. Assertions in the wrong test (nit)

The `listWorkflows` roles assertions are in `ForkWorkflowCopiesIntoAScopeAndReportsEveryRefusal`
(`tests/Weavie.Core.Tests/Mcp/RegistryMcpTests.cs:467`). The coverage is real; the listing test
`WorkflowToolsListPublishTheSchemaAndValidateByIdOrText` (`:380`) is where a reader looks for it.

**Fixed.** The assertions moved to `WorkflowToolsListPublishTheSchemaAndValidateByIdOrText`.

### 17. Design error table (nit)

[design.md](design.md)'s error table doesn't list `Workflow <id> asks for no role "<role>".` or
`<Name>'s models aren't known yet, so its roles can't be picked.`, both added by the fixes.

**Fixed.** The table lists both refusals, and the pick pseudo-code shows `Save` after `Prepare`.

## Checked and sound

- A role can't run on the session's own model: `WorkflowDefinition.AgentOf` is the only lookup, the runner throws for a
  role the run doesn't hold, and terminal providers are refused before any turn.
- A `WorkflowLaunch` is made only by `WorkflowCatalog.Prepare`.
- The JSON is source-generated throughout, with `set` on properties that carry initializers.
- The `AcpControlStore` extraction keeps its behaviour, and `SettledAsync` returns on dispose.
