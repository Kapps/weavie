# Workflow editing tools: tasks

Groups are in build order. Each builds and tests on its own.

## Group 1: Validate by id or text

Satisfies R1, R2, R3.

- [x] Add `WorkflowReader.ReadProposal` over a shared private `Read`.
- [x] Extract `WorkflowCatalog.Resolve` and have `Winner` use it.
- [x] Add `Validate(id)` and `ValidateText`; delete `Validate(path)`.
- [x] Update `IWorkflowRegistry`, `Unavailable`, and `SessionWorkflowRegistry`.
- [x] Change the `validateWorkflow` schema and handler, with the exactly-one check.
- [x] Replace the path test and add the catalog and MCP tests for R1 to R3.
- [x] Refuse any other `validateWorkflow` argument, and say structural errors come first in its description.

**Video:** in the real app, an agent validates a broken saved workflow by `id`, then a broken and a fixed proposal as
`text`, and the file on disk stays unchanged throughout; calling with both arguments shows the error.

## Group 2: Fork and folders

Satisfies R4, R5, R6.

- [x] Add `WorkflowCatalog.Folder` and `Fork`.
- [x] Update `IWorkflowRegistry`, `Unavailable`, and `SessionWorkflowRegistry`.
- [x] Add the `forkWorkflow` tool, handler, scope parsing, and dispatch case.
- [x] Add `folders` to `listWorkflows`.
- [x] Add the catalog and MCP tests for R4 to R6.
- [x] Check in the real app whether a repository fork appears in review, and record the result in this spec.

**Video:** in the real app, an agent lists the folders, forks the `quick-fix` built-in into personal and then
repository, shows `listWorkflows` marking the originals as shadowed, and is refused when forking again and when forking
a broken workflow. The clip also shows what review displays for the repository fork.

## Group 3: Prompt, guidance, and docs

Satisfies R7.

- [x] Rewrite the `AuthorWorkflowPrompt` steps as the overview shows.
- [x] Add the `listWorkflows` rule to `EmbeddedAgentGuidance`.
- [x] Update the `validateWorkflow` and `getWorkflowSchema` descriptions.
- [x] Update `docs/specs/workflows.md`, including "Built so far", Authoring, and forking.
- [x] Add the tests for the prompt and guidance rules.

**Video:** in the real app, asking an agent to change a workflow's stage shows `runPrompt`, then `listWorkflows`,
`validateWorkflow` with `text`, and a normal edit in the agent pane, with the changed workflow still valid afterwards.
