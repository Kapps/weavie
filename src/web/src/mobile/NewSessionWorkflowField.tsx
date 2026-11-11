import { ChevronDown } from "lucide-solid";
import { createEffect, createMemo, createSignal, type JSX, onCleanup, Show } from "solid-js";
import { Portal } from "solid-js/web";
import {
  backendPhase,
  launchSource,
  requestWorkflowCatalog,
  requestWorkflowSuggestion,
} from "../bridge";
import { ControlMenu, controlMenuPlacement } from "../chrome/ControlMenu";
import {
  NewSessionWorkflowChoice,
  type WorkflowChoiceState,
} from "../chrome/new-session-workflow-choice";

export interface NewSessionWorkflowActions {
  /** The workflow to start with, as shown now. */
  capture: () => string | null;
  flush: () => void;
  reset: () => void;
}

const NONE = "";
const NONE_HINT = "Tell your agent how you like to work and ask it to create a workflow around it.";

/** The workflow a new session starts in: suggested once from the prompt, and the user's to change. */
export function NewSessionWorkflowField(props: {
  active: boolean;
  backendId: string;
  base: "source" | "main";
  prompt: string;
  /** Whether the field has read what it can offer, so a start would run what it shows. */
  onReady: (ready: boolean) => void;
  register: (actions: NewSessionWorkflowActions) => void;
}): JSX.Element {
  const [state, setState] = createSignal<WorkflowChoiceState>({
    options: [],
    workflow: null,
    problem: null,
    status: "loading",
  });
  const [anchor, setAnchor] = createSignal<DOMRect | null>(null);
  const choice = new NewSessionWorkflowChoice(
    (target, signal) => requestWorkflowCatalog(target.backendId, target.sourceId, signal),
    (target, prompt, signal) =>
      requestWorkflowSuggestion(target.backendId, target.sourceId, prompt, signal),
    (next) => {
      setState(next);
      props.onReady(next.status !== "loading");
    },
  );
  props.register({
    capture: () => choice.capture(),
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
      choice.retarget(online() ? { backendId: props.backendId, sourceId: sourceId() } : null);
    } else {
      setAnchor(null);
    }
  });
  createEffect(() => choice.draft(props.prompt.trim()));
  onCleanup(() => choice.cancel());

  const label = (): string => state().workflow ?? "No workflow";

  return (
    <div class="session-composer-workflow">
      <span id="new-session-workflow-label">Workflow</span>
      <button
        type="button"
        class="session-composer-workflow-toggle ui-field"
        aria-labelledby="new-session-workflow-label new-session-workflow-value"
        aria-haspopup="listbox"
        aria-expanded={anchor() !== null}
        aria-busy={state().status === "suggesting"}
        disabled={state().status === "loading"}
        onFocus={() => choice.claim()}
        onBlur={() => choice.release()}
        onClick={(event) =>
          setAnchor((open) => (open === null ? event.currentTarget.getBoundingClientRect() : null))
        }
        onKeyDown={(event) => {
          if (event.key === "ArrowDown" && anchor() === null) {
            event.preventDefault();
            setAnchor(event.currentTarget.getBoundingClientRect());
          }
        }}
      >
        <span id="new-session-workflow-value" class="session-composer-workflow-value">
          {label()}
        </span>
        <Show when={state().status === "suggesting"}>
          <span class="ui-row-desc">Suggesting…</span>
        </Show>
        <span class="ui-icon" aria-hidden="true">
          <ChevronDown />
        </span>
      </button>
      <Show when={state().problem}>{(problem) => <small role="alert">{problem()}</small>}</Show>
      <Show when={anchor()} keyed>
        {(rect) => (
          <Portal>
            <ControlMenu
              axis={{
                label: "Workflow",
                value: state().workflow ?? NONE,
                options: [
                  { id: NONE, label: "No workflow", description: NONE_HINT, group: null },
                  ...state().options.map((option) => ({
                    id: option.id,
                    label: option.id,
                    description: option.error ?? option.description,
                    group: null,
                    unavailable: option.error !== null,
                  })),
                ],
              }}
              class="session-composer-workflow-menu"
              // As wide as the field, so a long description wraps instead of running off a phone.
              style={{ ...controlMenuPlacement(rect), "max-width": `${rect.width}px` }}
              inside=".session-composer-workflow-menu, .session-composer-workflow-toggle"
              onPick={(id) => {
                setAnchor(null);
                choice.pick(id === NONE ? null : id);
              }}
              onClose={() => setAnchor(null)}
            />
          </Portal>
        )}
      </Show>
    </div>
  );
}
