# Workflow editing tools: requirements

From [brief.md](brief.md) and [overview.md](overview.md). "Agent" means an embedded agent, terminal Claude or ACP.

## R1. Validate a saved workflow by id

As an agent, I want to check a saved workflow by name, so that I can report what is wrong with a file someone else
changed.

1. WHEN `validateWorkflow` is called with `id`, THE tool SHALL report every problem in the saved workflow that wins for
   that id, with its path, line, and column.
2. WHEN the winning file is broken, THE tool SHALL report its problems rather than refusing it.
3. WHEN no workflow has that id, THE tool SHALL return an error listing the workflows that exist.
4. WHEN the winner is a built-in, THE tool SHALL validate its embedded text.

## R2. Validate a proposal before saving

As an agent, I want to check proposed YAML before saving it, so that a bad version never reaches the file.

1. WHEN `validateWorkflow` is called with `text`, THE tool SHALL report every problem in that text, including warnings.
2. WHEN `validateWorkflow` is called with `text`, THE tool SHALL NOT read, create, or change any file.
3. WHEN the text declares a valid `id`, THE tool SHALL NOT report a file-name mismatch.
4. IF the text's `id` is missing or malformed, THEN THE tool SHALL report that as an ordinary error.
5. IF the text is not parseable YAML, THEN THE tool SHALL report the parse error with its position.

## R3. One way to call validate

As an agent, I want one clear contract, so that I can't check the wrong thing.

1. IF `validateWorkflow` is called with neither `id` nor `text`, or with both, THEN THE tool SHALL return an error
   saying exactly one is required.
2. THE tool SHALL NOT accept `path`, and its schema SHALL list only `id` and `text`.

## R4. Fork a workflow

As an agent, I want to copy a workflow into a more specific scope, so that I can change a built-in or personal workflow
without touching the original.

1. WHEN `forkWorkflow` is called with a valid workflow's `id` and a more specific `scope`, THE tool SHALL write that
   workflow's exact text to `<id>.yaml` in that scope's folder and return the path.
2. WHEN the scope's folder doesn't exist, THE tool SHALL create it.
3. WHEN the source is a built-in, THE tool SHALL fork its embedded text.
4. WHEN a fork succeeds, `listWorkflows` SHALL show the new file as the winner and the source as shadowed by it.
5. THE tool SHALL be advertised without a read-only hint.

## R5. Forks refuse unsafe copies

As a user, I want forks to never damage or replace my files.

1. IF the workflow is broken, THEN `forkWorkflow` SHALL refuse and list its errors, writing nothing.
2. IF `scope` is not more specific than the winning source's scope, THEN `forkWorkflow` SHALL refuse, writing nothing.
3. IF anything already exists at the target, THEN `forkWorkflow` SHALL fail and leave it unchanged.
4. IF `id` is unknown, THEN `forkWorkflow` SHALL return an error listing the workflows that exist.
5. IF `id` or `scope` is missing, or `scope` is not `personal` or `repository`, THEN `forkWorkflow` SHALL return an
   error.

## R6. Report the real folders

As an agent, I want the folders this host reads, so that a new workflow lands where it will be found.

1. WHEN `listWorkflows` is called, THE tool SHALL return `folders` with the `personal` and `repository` paths.
2. THE paths SHALL be the folders the catalog reads, even when they don't exist yet or the host uses a custom Weavie
   root.

## R7. Teach the editing rules

As a user, I want agents to change workflows the same safe way every time.

1. THE `author-workflow` prompt SHALL tell agents to find a file through `listWorkflows` and to use its reported folders
   for a new workflow, never an assumed path.
2. THE prompt SHALL tell agents to pass the complete proposed YAML to `validateWorkflow` as `text` and fix every error
   before saving.
3. THE prompt SHALL tell agents to save with their normal edit or write tool, not a script.
4. THE prompt SHALL tell agents to use `forkWorkflow` and never edit a built-in in place.
5. THE embedded agent guidance SHALL tell agents to find workflows through `listWorkflows`.
6. THE `validateWorkflow` and `getWorkflowSchema` descriptions SHALL match these rules.
7. `docs/specs/workflows.md` SHALL describe the new tools and rules, and SHALL NOT mention `validateWorkflow(path)`.
