import { createEffect, createMemo, createSignal, Index, type JSX, onCleanup, Show } from "solid-js";
import {
  backendPhase,
  launchSource,
  type RolePick,
  requestRoleControls,
  requestRoleSuggestion,
  requestWorkflowCatalog,
  requestWorkflowSuggestion,
} from "../bridge";
import { agentProviders } from "../chrome/agent-default";
import { ControlBar } from "../chrome/ControlBar";
import {
  NewSessionRoleChoice,
  type RoleChoiceState,
  rolesReady,
} from "../chrome/new-session-role-choice";
import {
  NewSessionWorkflowChoice,
  type WorkflowChoiceState,
  type WorkflowTarget,
} from "../chrome/new-session-workflow-choice";
import { NewSessionPicker } from "./NewSessionPicker";

/** What a start runs: the workflow (null for none) and what to save for each role it still needs. */
export interface NewSessionWorkflowStart {
  workflow: string | null;
  roles: Record<string, RolePick>;
}

export interface NewSessionWorkflowActions {
  /** The workflow and role models to start with, as shown now. */
  capture: () => NewSessionWorkflowStart;
  flush: () => void;
  reset: () => void;
}

const NONE = "";
const NONE_HINT = "Tell your agent how you like to work and ask it to create a workflow around it.";

/**
 * The workflow a new session starts in, suggested once from the prompt and the user's to change, and under it the
 * roles it needs that the chosen provider can't yet fill, each with the agent's model, effort, and Fast Mode pickers.
 */
export function NewSessionWorkflowField(props: {
  active: boolean;
  backendId: string;
  base: "source" | "main";
  providerId: string;
  prompt: string;
  /** Whether a start would run what the field shows: its catalog is read and every role has a model. */
  onReady: (ready: boolean) => void;
  register: (actions: NewSessionWorkflowActions) => void;
}): JSX.Element {
  const [state, setState] = createSignal<WorkflowChoiceState>({
    options: [],
    workflow: null,
    problem: null,
    status: "loading",
  });
  const [roles, setRoles] = createSignal<RoleChoiceState>({ rows: [], error: null });
  let target: WorkflowTarget | null = null;
  const choice = new NewSessionWorkflowChoice(
    (to, signal) => requestWorkflowCatalog(to.backendId, to.sourceId, to.providerId, signal),
    (to, prompt, signal) =>
      requestWorkflowSuggestion(to.backendId, to.sourceId, to.providerId, prompt, signal),
    setState,
  );
  const roleChoice = new NewSessionRoleChoice(
    (to, model, signal) => requestRoleControls(to.backendId, to.providerId, model, signal),
    (pair, role, signal) =>
      requestRoleSuggestion(
        pair.target.backendId,
        pair.target.sourceId,
        pair.target.providerId,
        pair.workflow,
        role,
        signal,
      ),
    setRoles,
  );
  props.register({
    capture: () => ({ workflow: choice.capture(), roles: roleChoice.capture() }),
    flush: () => choice.flush(),
    reset: () => choice.reset(),
  });

  const online = createMemo(() => backendPhase(props.backendId) === "online");
  const sourceId = createMemo(
    () => launchSource(props.backendId, props.base)?.address.slot ?? null,
  );
  // Opening Sessions re-reads the catalog, so a workflow written since the last visit is offered.
  createEffect(() => {
    if (props.active) {
      target = online()
        ? { backendId: props.backendId, sourceId: sourceId(), providerId: props.providerId }
        : null;
      choice.retarget(target);
    }
  });
  createEffect(() => choice.draft(props.prompt.trim()));
  createEffect(() => {
    const shown = state().options.find((option) => option.id === state().workflow);
    roleChoice.show(
      shown === undefined || target === null
        ? null
        : { target, workflow: shown.id, roles: shown.roles },
    );
  });
  createEffect(() => props.onReady(state().status !== "loading" && rolesReady(roles())));
  onCleanup(() => {
    choice.cancel();
    roleChoice.cancel();
  });

  const providerName = (): string =>
    agentProviders(props.backendId).find((provider) => provider.id === props.providerId)?.name ??
    props.providerId;
  const startable = () => state().options.filter((option) => option.error === null);
  const broken = () => state().options.filter((option) => option.error !== null);

  return (
    <>
      <NewSessionPicker
        id="new-session-workflow"
        label="Workflow"
        value={state().workflow ?? "No workflow"}
        selected={state().workflow ?? NONE}
        options={[
          { id: NONE, label: "No workflow", description: NONE_HINT, group: null },
          ...startable().map((option) => ({
            id: option.id,
            label: option.id,
            description: option.description,
            group: null,
          })),
          ...broken().map((option) => ({
            id: option.id,
            label: option.id,
            description: option.error,
            group: "Unavailable",
            unavailable: true,
          })),
        ]}
        busy={state().status === "suggesting" ? "Suggesting…" : null}
        disabled={state().status === "loading"}
        problem={state().problem}
        open={props.active}
        onFocus={() => choice.claim()}
        onBlur={() => choice.release()}
        onPick={(id) => choice.pick(id === NONE ? null : id)}
      />
      <Show when={roles().rows.length > 0}>
        <section class="session-composer-roles" aria-labelledby="new-session-roles-label">
          <span id="new-session-roles-label" class="ui-section-label">
            Roles for {state().workflow}
          </span>
          <span class="session-composer-roles-note">
            Start saves these for all your {providerName()} sessions. Ask your agent to change them
            later.
          </span>
          <Show when={roles().error}>{(problem) => <small role="alert">{problem()}</small>}</Show>
          <Index each={roles().error === null ? roles().rows : []}>
            {(row) => (
              <div class="session-composer-role">
                <span>{row().role}</span>
                <ControlBar
                  label={`${row().role} role`}
                  axes={row().axes}
                  placeholders={["Model"]}
                  disabled={false}
                  busy={row().pending}
                  menuClass="session-composer-workflow-menu"
                  onPick={(axisId, value) => roleChoice.pick(row().role, axisId, value)}
                />
                <Show when={row().suggesting}>
                  <span class="ui-row-desc">Suggesting…</span>
                </Show>
                <Show when={row().problem}>
                  {(problem) => <small role="alert">{problem()}</small>}
                </Show>
              </div>
            )}
          </Index>
        </section>
      </Show>
    </>
  );
}
