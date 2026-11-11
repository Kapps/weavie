# New-session workflow picker — brief

## Goal

Let the user start a session in a workflow from the new-session composer: a workflow field beside the branch
field, suggested from the prompt, with the prompt (and its images) handed to the workflow as written.

## Decisions

### The run's prompt

- Every run has a **prompt**: the text and images the run was started with. Stage templates reference it as
  `{{run.prompt}}`.
- Images belong to the prompt. They are saved in the run's shared documents folder (the session's pasted-images
  folder is wiped on every unload, so it cannot hold them) and follow that folder's lifetime. They are sent to the
  agent once, with the first stage turn that uses `{{run.prompt}}` (a skill stage included), and survive unload,
  reload, and restart.
- A start is **workflow + prompt**; the "a workflow or a prompt, not both" rule is removed.
- `weavie.session.new` and `weavie.workflow.run` gain `prompt` and keep `inputs`, so an agent can still start a
  workflow that declares inputs.
- The built-in `quick-fix` and `spec-driven`, and the user's personal `feature-build`, replace their single text
  input with `{{run.prompt}}` and declare no inputs. No compatibility path for the old form.

### The workflow field

- One dropdown beside the branch field in the shared composer (desktop Sessions modal and mobile), built on the
  existing custom menu component (`src/web/src/chrome/ControlMenu.tsx`, the agent pane's option picker), since a
  native dropdown can show neither an icon nor a reason on touch. First entry is *No workflow*, the initial value unless `workflow.default` names one.
- The field is always present, built-ins included, whether or not inference is on, so the workflow system is
  discoverable; only the suggestion depends on inference.
- It lists the catalog a start resolves today: the chosen host's, read from the current session's worktree when
  starting from it and from the workspace checkout otherwise, re-read when either choice changes. The list, the
  suggestion, and Start share that one resolver.
- A workflow that **declares inputs** is not a starting workflow and is not listed.
- Every entry is a name and one line under it: a workflow's description, and for *No workflow* a pointer to asking
  the agent to create one. **Invalid** workflows are listed last under an *Unavailable* heading, greyed out, with
  the error as that line. They cannot be selected.
- Reached by Tab between the branch field and Start. No new command, shortcut, or "suggest again" control.

### Suggestion

- The suggestion is a **decision**, not a text query: the live decision provider picks among *none* and each listed
  valid workflow (its description is the choice's criteria), with the prompt text as evidence. Branch naming stays
  its own text query; the two run independently.
- It runs **once**, when the prompt has 16 words and then pauses or loses focus, and is never recalculated as the
  prompt changes. A shorter prompt never spends it, whatever the focus does; a typed branch name does not stop it.
- It is not asked when inference is turned off in settings or the catalog offers no startable workflow; neither is
  a failure, so neither shows a message.
- The suggestion replaces the initial value (including with *No workflow*) only when its probability reaches
  `workflow.decisionConfidence`; below that the field is left as it was, with no message.
- A failed decision (provider error, timeout) leaves the field as it was and shows the reason by the field, as
  branch naming does.
- Once the user picks or clears the field it is theirs; a suggestion never overwrites it.

### Submitting

- Start is unavailable until the field has read its catalog, so it never runs without the configured default.
- Start uses the workflow field's value at the moment it is pressed. It never waits for a pending suggestion, and
  one landing while Start waits for the branch name is discarded, so a submit cannot launch a workflow the user
  never saw selected.
- With a workflow selected the composer sends workflow + prompt + images through `weavie.session.new`; with none,
  it behaves as today.

## Edge cases

- An agent sets `workflow.default`, like every workspace-scoped setting, in the workspace's own settings whichever
  session it runs in; a session's worktree has none of its own.
- `workflow.default` naming a workflow that is missing, invalid, or declares inputs: the field shows *No workflow*
  with the reason by the field.
- Switching host or starting point keeps the selected workflow when the new catalog offers it, and otherwise
  falls back to that catalog's initial value. The one suggestion belongs to the draft, so no switch asks again.
- A prompt that never reaches 16 words, or is images only, gets no suggestion; the field stays at its initial value.
- A workflow that never references `{{run.prompt}}` still starts; the prompt is simply unused, like an unused input.
- A workflow that references `{{run.prompt}}` started by a command with no prompt fails to start with a message naming
  the missing prompt, leaving no session behind.
- A run saved before this change has no prompt; it loads with an empty one and carries on rather than being set
  aside.
- The *none* choice's label cannot collide with a workflow id (`none` is a legal id).
- `feature-build` is edited in the user's personal folder, outside the PR. A live run keeps the definition text it
  started with, so the edit cannot break one.

## Out of scope

- Input fields in the composer, extracting inputs from the prompt, and starting input-declaring workflows from the
  composer.
- Re-suggesting the workflow, and any default keybinding.
- Child workflows, `fork` / `fresh` contexts, run history, and workflow suggestions outside the composer.
- Any other change to the standard UI.

## Approach

- **Engine (`Weavie.Workflows`)**: a run stores its prompt (text plus image paths) beside its inputs, renders
  `{{run.prompt}}` in templates, and reports which stage turns carry the images. Validation knows `{{run.prompt}}`.
  *Why:* the prompt is what the user asked for, so it must follow the run across stages and restarts rather than
  ride one opening message; making it a first-class value removes input mapping from the composer entirely.
- **Host (`HostCore`)**: session creation accepts workflow + prompt + attachments and saves the images into the new
  session before the run starts. Two new composer requests on the session-creation feature: the workflow catalog
  for a host and starting point (ids, descriptions, validity and errors, minus input-declaring workflows), and the
  workflow suggestion, which builds the decision from that catalog and applies the confidence threshold host-side.
  A `workflow.default` setting is registered. *Why:* all four hosts get it at once, and the web never restates
  catalog or threshold rules.
- **Web**: a workflow field component next to `NewSessionBranchField`. The composer owns one "draft settled"
  trigger (word gate, idle pause, focus leaving) that both the branch preview and the workflow suggestion consume.
  *Why:* one trigger keeps the two fields in step without copying the timer logic; the workflow side needs none of
  the branch field's retry or resolve-on-submit machinery.
- **Docs and guidance**: `docs/specs/workflows.md` ("Starting a run", format, built-in examples), the
  `author-workflow` prompt, and the `weavie.session.new` / `weavie.workflow.run` command descriptions teach the
  prompt model; the `workflow.decisionConfidence` and `inference.decisionProvider` descriptions name the composer
  suggestion (a vendor provider receives the draft prompt).
