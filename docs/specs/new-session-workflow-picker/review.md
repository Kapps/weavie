# New-session workflow picker — review

Review of PR #1180 against [design.md](design.md): one fresh-context pass by the `weavie-reviewer` agent, given
only the design and the branch diff, and a duplication pass over the whole diff.

## Findings

| # | Severity | Finding | Status |
| --- | --- | --- | --- |
| 1 | bug | Three mobile e2e tests failed in CI: the composer now also asks `suggestWorkflow`, and the fake `claude` answered a decision query with a branch reply outside its `workflow` mode, so a "Workflow suggestion failed" alert appeared beside the branch field's | fixed: the fake answers a decision in every answering mode with an answer no caller is confident in; the failed-inference test asserts each field's own alert |
| 2 | warn | The mock-host spec and the functional spec shared the name `new-session-workflow.spec.ts`, so the `chromium` project also ran the functional one | fixed: the mock-host spec is `new-session-workflow-field.spec.ts` |
| 3 | warn | Moving focus onto the workflow field spent the one suggestion and the pick then aborted it | fixed: focus landing in the workflow field asks nothing, as the branch field already does for its own |
| 4 | warn | A draft that settled while the catalog was unread was asked about later, half-edited | fixed: an edited draft has to settle again; unit test added |
| 5 | warn | No host test proved that a refused send pauses the run still owing its images; the design's claim also held only while an ACP conversation is ready | fixed: host test with an agent that refuses the turn; the design states the not-ready case |
| 6 | nit | Start before the catalog was read silently ignored `workflow.default` | fixed: Start waits for the field's first read |
| 7 | nit | A failed catalog re-read discarded the user's pick | fixed: a failed read changes only the reason shown; unit test added |
| 8 | nit | The host built the launch catalog in two places; the mock-host spec re-declared the new-session command | fixed: one `LaunchRoot` rule feeds both; `NEW_SESSION_COMMAND` is shared from `mock-host.ts` |
| 9 | nit | The typed-turn order of a command with images was untested | fixed: Core test of `TerminalParts()` for a command and a prompt |
| 10 | nit | A keyboard user on desktop could not read why a workflow is unavailable | fixed: the reason shows in the row while it is highlighted; browser test asserts it |
| 11 | nit | A repository workflow's description could be written to win the suggestion | rejected: the field shows the workflow before Start, and repository trust is already a deferred item in `workflows.md` |
| 12 | nit | Wording: a design heading and a test comment described the path rather than the state | fixed, except the design's ticked build checklist, which the build workflow asks for |

## Found while proving it in the real app

| # | Severity | Finding | Status |
| --- | --- | --- | --- |
| 13 | bug | `workflow.default` set by an agent in a worktree session was ignored: the session's registry kept workspace-scoped settings in an overlay keyed by its own worktree, which nothing reads (`test.profile` and `worktree.*` went the same way) | fixed: the registry routes them to the workspace's overlay; Core test and a full-stack test |
| 14 | bug | One workflow file that makes YamlDotNet's scanner throw the wrong exception failed the whole catalog, emptying the picker | fixed: the file is listed as invalid; reader test |

## Outside the diff

`native build (macos)` fails in CI with launch error `-10825` on `Weavie.Mac.Tests.app`. It fails the same way on
another branch started at the same time, with the macOS SDK pack at `27.2`; nothing in this diff touches Mac code.
