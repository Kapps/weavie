import { createEffect, For, type JSX, Show } from "solid-js";
import { AgentControlPicker } from "./AgentControlPicker";
import { AgentModelPicker } from "./AgentModelPicker";
import {
  agentControlState,
  closeControlPicker,
  MODEL_AXIS,
  openControlPicker,
} from "./agent-controls-store";

// The dim strip under the composer. First segment is the merged model → effort / Fast control (its picker is a
// cascading per-model submenu); the rest are the approvals / sandbox axes. Values are provider-neutral — the web
// never learns they came from Codex.
export function AgentStatusLine(props: { backendId: string; slot: string | null }): JSX.Element {
  const state = (): ReturnType<typeof agentControlState> => agentControlState(props.slot);
  const modelLabel = (): string => state().modelControl.valueLabel;
  const hasModel = (): boolean => state().modelControl.models.length > 0;
  // Switching sessions abandons an open picker so it can't apply to the wrong session.
  createEffect(() => {
    props.slot;
    closeControlPicker();
  });

  return (
    <Show when={hasModel() || state().axes.length > 0}>
      <div class="agent-status-line">
        <Show when={hasModel()}>
          <button
            type="button"
            class="agent-status-segment agent-status-model"
            title={`Model — ${modelLabel()} — click to change model, effort, or Fast Mode`}
            onClick={() => openControlPicker(MODEL_AXIS)}
          >
            <span class="agent-status-value">{modelLabel()}</span>
          </button>
        </Show>
        <For each={state().axes}>
          {(axis) => (
            <button
              type="button"
              class="agent-status-segment"
              title={`${axis.label}: ${axis.valueLabel} — click to change`}
              onClick={() => openControlPicker(axis.id)}
            >
              <span class="agent-status-key">{axis.label}</span>
              <span class="agent-status-value">{axis.valueLabel}</span>
            </button>
          )}
        </For>
        <AgentModelPicker backendId={props.backendId} slot={props.slot} />
        <AgentControlPicker backendId={props.backendId} slot={props.slot} />
      </div>
    </Show>
  );
}
