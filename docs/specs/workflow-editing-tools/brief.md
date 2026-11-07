# Workflow editing tools: brief

## Problem

Agents change Weavie workflows by following the `author-workflow` prompt, which says to write the YAML file and then
call `validateWorkflow`. That route works, but it has real gaps:

- **The prompt only covers creating.** It says almost nothing about changing an existing workflow, so agents pick
  their own method. One session used a Python find-and-replace on `~/.weavie/workflows/feature-build.yaml` as its first
  and only attempt; nothing had blocked it.
- **A bad version reaches the file before it is checked.** `validateWorkflow` only reads a file already on disk. Until
  it is fixed, an invalid workflow can't start.
- **Built-ins can't be forked.** The prompt says to fork, but a built-in exists only as `built-in:<id>.yaml`, so an
  agent has no text to copy.
- **The prompt hardcodes `~/.weavie/workflows`.** On a preview host the personal folder is elsewhere, so a new workflow
  can land where the host never looks. One session had to symlink its file into `$WEAVIE_ROOT/workflows`.

## Decision

Agents keep editing workflow YAML with their normal edit tools. Weavie does not get an edit tool: a find-and-replace
tool would rebuild the agent's Edit, only worse, and would bypass the Edit and Write tools Weavie tracks. Instead
Weavie checks proposals before they are saved, makes forking possible, and reports the real folders.

## Users

Embedded agents (terminal Claude and ACP) working on a user's request to create, fork, change, or improve a workflow.

## Requirements

### `validateWorkflow` takes `id` or `text`

- Exactly one of them; passing neither or both is an error.
- `id` checks the saved workflow that wins for that id, including a broken one, such as a file the user edited by hand.
- `text` checks proposed YAML without touching any file, under the `id` the text declares.
- `path` is removed.

### `forkWorkflow(id, scope)`

- Copies a valid workflow, including a built-in, into the personal or repository folder and returns the path.
- Refuses a broken workflow and lists its errors; a broken copy in a more specific scope would become the winner.
- Only copies into a more specific scope than the source, and keeps the same id.
- Refuses if a file is already at the target; it never overwrites.
- A repository fork lands in the session's worktree, as an ordinary change to commit.

### `listWorkflows` reports the folders

It also returns the personal and repository workflow folders for this host and worktree, so a new workflow lands where
the host reads it.

### Prompt and guidance

`author-workflow` and `EmbeddedAgentGuidance` say:

- Find a workflow's file through `listWorkflows`; use the reported folders for a new one, never a hardcoded path.
- Always validate first: pass the complete proposed YAML to `validateWorkflow` as `text` and fix every error before
  saving.
- Save with the normal edit or write tool, not a script.
- Fork with `forkWorkflow`; never edit a built-in in place.
- A variant under a new id is a new file the agent writes in the reported folder.

`docs/specs/workflows.md` is updated to match.

## Edge cases

- **Runs in flight:** unaffected; a run executes the definition snapshot it started with.
- **Already-broken file:** `id` reports its problems, and a fixed version is proposed as `text` like any other change.
- **Fork from a shadowed workflow:** `id` resolves to the winner, so the scope rule applies to the winning copy.
- **The user edits a file by hand:** saving is still never blocked; validation stays advisory.

## Constraints

- Nothing enforces the rules. A hook can't stop a shell script, ACP agents only reach Weavie when they ask for
  permission, and a block can't tell when the user explicitly asked for a direct edit.
- Host and client ship together, so `path` is deleted outright with no compatibility path.
- Native AOT-safe and registered in Core, like the existing workflow tools.

## Out of scope

- `editWorkflow` or `writeWorkflow` tools.
- Delete and rename tools.
- Any hook or block on direct edits.
- Forking under a new id.
- The Edit Workflow command and inline YAML errors.
