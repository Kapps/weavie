# Workflow editing tools: detailed design

Implements [requirements.md](requirements.md). [overview.md](overview.md) holds the flow, wire examples, and pseudo
implementations; this document fixes the details those leave open.

## Interfaces

`WorkflowCatalog` (`src/Weavie.Workflows/WorkflowCatalog.cs`):

| Member | Rule |
| --- | --- |
| `Validate(string id)` | `Resolve(id).Diagnostics`; replaces `Validate(string path)` |
| `ValidateText(string text)` | `WorkflowReader.ReadProposal(text).Diagnostics` |
| `Fork(string id, WorkflowScope scope)` | Writes the winner's `SourceText`; returns the path |
| `Folder(WorkflowScope scope)` | The catalog's own directory for `Personal` or `Repository` |
| `Resolve(string id)` (private) | The winning entry, valid or not; throws for an unknown id |

`WorkflowReader.ReadProposal(string text)` shares a private `Read(string, Func<WorkflowDefinition, string>)` with
`Read(string, string)`.

`IWorkflowRegistry` gains the same four public members. `SessionWorkflowRegistry` forwards to its catalog and
`WorkflowRegistry.None` throws `ToolUnavailableException`, which `HandleToolCallAsync` already reports as a tool error.

## Tool schemas

| Tool | Arguments | Hints |
| --- | --- | --- |
| `validateWorkflow` | `id`: string, `text`: string; neither in `required`, because exactly one is checked by the handler | read-only |
| `forkWorkflow` | `id`: string, `scope`: enum `personal`, `repository`; both required | none |
| `listWorkflows` | none; reply gains `folders` | read-only |

`forkWorkflow` is a private constant in `McpServer.WorkflowTools.cs` like `ValidateWorkflowTool`, with its dispatch case
in `McpServer.cs`.

## Data model

No new types and no persisted state. `WorkflowScope` order (`BuiltIn < Personal < Repository`) is what "more specific"
compares, so the existing enum carries the fork rule. `folders` is written first in the `listWorkflows` object.

Fork copies `SourceText` byte for byte: comments and folded prompts survive, and the copy's `id` already equals its new
file name.

## Error handling

Every refusal is a tool error whose message tells the agent what to do. Nothing falls back.

| Condition | Source | Message shape |
| --- | --- | --- |
| Neither or both of `id`, `text`, or any other argument | handler | `validateWorkflow takes exactly one of 'id' or 'text', and nothing else.` |
| Missing `id` or `scope`, or a bad `scope` | handler | `forkWorkflow requires 'id' and a 'scope' of personal or repository.` |
| Unknown id | `Resolve` | Today's `No workflow "<id>" exists; the workflows are: ...` |
| Fork of a broken workflow | `Winner` | Today's `Workflow "<id>" (<path>) is invalid: ...` |
| Scope not more specific | `Fork` | Names the id and the source's scope |
| Target exists, or a write fails | `File.Open` with `CreateNew` | The `IOException` or `UnauthorizedAccessException` message |

Both handlers catch `WorkflowException`, `IOException`, and `UnauthorizedAccessException` and send the message.
`Folder(BuiltIn)` throws `WorkflowException`; only an internal mistake reaches it, because `scope` parsing and the
ordering check exclude built-ins.

The normal overwrite case never reaches `File.Open`: a file at the target would be the winner, so the scope check
refuses first. `CreateNew` covers what the catalog doesn't list, such as a directory at that path, and a race between
two forks.

## Testing strategy

Unit tests in `tests/Weavie.Workflows.Tests/WorkflowCatalogTests.cs` use the existing `Catalog()` fixture over a temp
directory:

- `Validate`: valid, broken winner, built-in, unknown id, winner chosen over a shadowed broken copy.
- `ValidateText`: valid, warnings, missing `id`, malformed `id`, unparseable YAML; no file appears on disk.
- `Fork`: built-in to personal, built-in to repository, personal to repository, exact text preserved, folder created,
  new file wins in `List()`.
- `Fork` refusals: broken source, same scope, less specific scope, unknown id, and a directory named `<id>.yaml` at the
  target; nothing is written in each case.
- `Folder`: both scopes; built-in throws.

MCP tests in `tests/Weavie.Core.Tests/Mcp/RegistryMcpTests.cs` drive the real server:

- `validateWorkflow` with `id`, with `text`, with neither, with both, or with any other argument; its schema has no
  `path`.
- `forkWorkflow` success, refusal, missing arguments, bad scope; no read-only hint.
- `listWorkflows` returns `folders`.
- `author-workflow` and the guidance contain the rules that R7 names.

Proof in the real app, with a fake agent at the process seam and an isolated Weavie root, covers the tools end to end and
answers the open question: whether a repository fork written by the host appears in review.

## Rollout

Host and client ship together, so `path` is deleted with no compatibility path. Nothing in `src/web`, the commands, or
the hosts calls it.
