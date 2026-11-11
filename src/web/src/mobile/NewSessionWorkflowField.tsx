import { createEffect, createMemo, createSignal, Index, type JSX, onCleanup } from "solid-js";
import {
  backendPhase,
  launchSource,
  requestRoleModels,
  requestRoleSuggestion,
  requestWorkflowCatalog,
  requestWorkflowSuggestion,
} from "../bridge";
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

/** What a start runs: the workflow (null for none) and the model to save for each role it still needs. */
export interface NewSessionWorkflowStart {
  workflow: string | null;
  roles: Record<string, string>;
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
 * The workflow a new session starts in, suggested once from the prompt and the user's to change, and a model
 * picker for each role it needs that the chosen provider can't yet fill.
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
  const [roles, setRoles] = createSignal<RoleChoiceState>({ rows: [], models: [], error: null });
  let target: WorkflowTarget | null = null;
  const choice = new NewSessionWorkflowChoice(
    (to, signal) => requestWorkflowCatalog(to.backendId, to.sourceId, to.providerId, signal),
    (to, prompt, signal) =>
      requestWorkflowSuggestion(to.backendId, to.sourceId, to.providerId, prompt, signal),
    setState,
  );
  const roleChoice = new NewSessionRoleChoice(
    (to, signal) => requestRoleModels(to.backendId, to.providerId, signal),
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
      <Index each={roles().rows}>
        {(row) => (
          <NewSessionPicker
            id={`new-session-role-${row().role}`}
            label={row().role}
            value={
              row().model === null
                ? roles().models === null
                  ? "Loading models…"
                  : "No model"
                : (roles().models?.find((model) => model.id === row().model)?.label ?? row().model!)
            }
            selected={row().model ?? ""}
            options={roles().models ?? []}
            busy={row().suggesting ? "Suggesting…" : null}
            disabled={(roles().models?.length ?? 0) === 0}
            problem={row().problem ?? roles().error}
            open={props.active}
            onFocus={() => {}}
            onBlur={() => {}}
            onPick={(model) => roleChoice.pick(row().role, model)}
          />
        )}
      </Index>
    </>
  );
}
