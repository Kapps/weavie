# Workflow roles — brief

## Goal

Let a workflow ask for an agent by role (`planner`, `builder`, `reviewer`) without naming a provider or model, so
built-in and repository workflows stay portable and each user decides which model fills each role on each provider.

## Decisions

### What a role is

- A role is a **name the user owns**, with one implementation per session provider: a full agent (provider, model,
  options, all provider-native). `planner` is Fable in Claude sessions and Astra in Codex sessions for one user, Opus
  and Sol for another.
- A step asks for one with its own key, `role: planner`, exclusive with `agent`. `agent` and the workflow's `agents`
  are unchanged, so an undefined agent name still fails at load and a role never overrides a pinned agent.
- Workflows carry no description of a role, and Weavie ships no roles, descriptions, or default models. Names are
  free-form; `planner`, `builder`, and `reviewer` are a documented convention, not enforced.
- A role resolves through the session's provider, so a `continue` or `fork` step can never be handed another
  provider's model by a role.

### Where roles are kept and changed

- Roles have their own store, not the settings system: one user-wide file.
- One command sets or clears a role's implementation for a provider; a registry tool lists them. The agent uses
  both ("make Fable my planner", "what are my roles"), including options such as effort.
- The new-session composer shows a model picker **only for a role the selected workflow names that needs a model**
  on the chosen provider: none saved, or a saved model the provider no longer offers. With none needing one, nothing
  is shown. Start is unavailable until each shown role has a model.
- A pick is saved when Start is pressed. It sets the model and drops the role's previous options, since options
  depend on the model. The picker lists only the chosen provider's models.

### Suggestion

- With inference on, a role with nothing saved is suggested by the decision provider: one decision per role, whose
  evidence is the role's name and the prompts of the stages that use it, and whose choices are the provider's models
  with the provider's own descriptions.
- It prefills the picker visibly, only at `workflow.decisionConfidence`, never over the user's own pick, and is saved
  only by Start. Below the threshold the picker stays empty. A failed decision shows its reason by the field.
  Inference turned off, or a provider offering fewer than two models, asks nothing.

### Starting a run

- A run resolves its roles when it starts and keeps them: a later change to a role affects the next run. The pane's
  model picker still changes the open step, as today.
- A start outside the composer (Run Workflow, an agent's `weavie.session.new` or `weavie.workflow.run`) with a role
  that has no implementation for the session's provider is refused, naming the role, the provider, and the command
  that fills it. It leaves no run.
- A resolved role is checked and applied exactly like a pinned agent: the same refusals at run start and step open.
- The composer's workflow list knows the chosen provider: a workflow that provider can't run (a role or pinned
  agent on terminal Claude, a pinned agent of another provider) is listed under *Unavailable* with the reason, as a
  broken one is, and the workflow suggestion never picks it.

### Built-ins and guidance

- `spec-driven` uses `planner` for requirements, design, and tasks, and `builder` for implement, so it runs only in
  native agent sessions. `quick-fix` names no role and runs everywhere.
- The `author-workflow` prompt tells agents to use a role when stages need different strengths, to prefer the three
  conventional names, and to pin an agent only when the user names a model.
- `workflow-agent-selection.md` and `workflows.md` document roles and drop the deferred note.

## Edge cases

- **Terminal Claude**, and any provider that offers no model choice, can't run a workflow that names a role: it is
  unavailable in the composer and refused from a command, like one with a pinned agent. Terminal Claude is the
  default provider, so `spec-driven` is unavailable there.
- **A saved model the provider no longer offers** is never substituted. The composer shows that role's picker with
  a line naming the missing model; a command start is refused, naming the missing model and what the provider
  offers.
- **A model list still loading or failed** matters only when a role needs a pick: its state shows by the role and
  Start waits. With every role saved, Start works and each step is checked when it opens, as a pinned agent's is.
- **A role saved with another provider's model** (possible only through the command) is refused at start with the
  message a pinned other-provider agent gets today.
- **A typo in a role name** can't be caught at load. It shows in the composer as a role needing a model and in a
  command start's refusal; a role saved by mistake is cleared through the command.
- **Switching provider or workflow in the composer** re-reads the roles for the new pair; a suggestion belongs to
  the pair it was asked for.
- **A role with stale options** fails as a pinned agent's options do: at run start when checkable, otherwise when
  the step opens, pausing the run with the reason.

## Out of scope

- Running a step on another provider. Only the stored shape allows for it; nothing else is built.
- A per-repository override of a role. It is a second file with the same shape, added later.
- Shipped roles, role descriptions, and default models. An unfilled role is never run on the session's own model.
- Showing or changing already-saved roles in the composer, picking options or another provider's model there, and
  any role editor beyond the command.
- Roles on decisions or `check`; per-step models on terminal Claude.
- Any compatibility path for older workflows.
- The user's personal `feature-build` moves from its pinned `fable` and `opus` agents to `planner` and `builder`
  once the feature is running in their build; it lives outside the repository and the PR.

## Approach

- **Role store (Core)**: provider → role → agent, in one user file shaped like `AcpControlStore`, with one
  set/clear command and one listing tool. *Why:* settings keys must be declared and hold one scalar, and an agent's
  write to a workspace-scoped setting always lands in the repository's override, so free-form roles with a real
  user-wide value can't live there. It is JSON because Weavie writes it; only agent-authored workflows are YAML.
  Storing the provider with the model means cross-provider steps later need no format change.
- **Engine (`Weavie.Workflows`)**: a step takes `role` beside `agent`; the definition reports the roles it names.
  Preparing a launch takes a resolver the engine defines, so a launch can't exist with an unresolved role, and the
  resolved agents are persisted with the run and restored when it reloads. *Why:* the engine stays provider-free and
  store-free, resolution is enforced by construction instead of at each start site, and everything after start (the
  hold, step open, refusals) sees only agents, one path for pinned and role-filled alike.
- **Resolution (Core)**: one resolver turns a definition plus a session provider into agents or a refusal naming
  each role it couldn't fill; `WorkflowAgentControls` words it beside the existing refusals. The composer and every
  command start go through it. *Why:* one rule for what is missing and why, wherever a run starts.
- **Composer (HostCore + web)**: the workflow catalog request takes the chosen provider and returns, per workflow,
  whether that provider can run it and which roles need a model; the model list (or its loading/failed state) and
  the suggestion are separate requests. The web adds picker rows under the workflow field, and Start sends the
  picks to be saved before the run starts. *Why:* all hosts get it at once, and the web never restates catalog,
  threshold, or refusal rules.
- **Suggestion (Core)**: a sibling of `WorkflowSuggester` over `IDecisionService` and `AgentModelCatalog`. *Why:*
  choosing one id from an advertised list is exactly a decision, so the answer is always a provider-native id.
