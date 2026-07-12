import { createEffect, For, type JSX, Show } from "solid-js";
import type { AgentControlAxis } from "../bridge";
import { AgentControlPicker } from "./AgentControlPicker";
import {
  agentControlState,
  closeControlPicker,
  isToggleOn,
  openControlPicker,
  toggleAgentControl,
} from "./agent-controls-store";

// The dim strip under the composer showing the session's model / effort / speed / approvals / sandbox. A picker
// axis opens its listbox; a toggle axis is a one-click on/off chip (e.g. Fast Mode). The values are provider-neutral
// — the web never learns they came from Codex. Generic over axes, so a provider reporting different controls renders
// without a code change here.
export function AgentStatusLine(props: { backendId: string; slot: string | null }): JSX.Element {
  const state = (): ReturnType<typeof agentControlState> => agentControlState(props.slot);
  // Switching sessions abandons an open picker so it can't apply to the wrong session.
  createEffect(() => {
    props.slot;
    closeControlPicker();
  });

  // The chip shows the on-option's name (the mode name, e.g. "Fast") and highlights while active.
  const toggleLabel = (axis: AgentControlAxis): string => axis.options[1]?.label ?? axis.label;
  const onToggle = (axis: AgentControlAxis): void => {
    if (props.slot !== null) {
      toggleAgentControl(props.backendId, props.slot, axis);
    }
  };

  return (
    <Show when={state().axes.length > 0}>
      <div class="agent-status-line">
        <For each={state().axes}>
          {(axis) => (
            <Show
              when={axis.toggle}
              fallback={
                <button
                  type="button"
                  class="agent-status-segment"
                  title={`${axis.label}: ${axis.valueLabel} — click to change`}
                  onClick={() => openControlPicker(axis.id)}
                >
                  <span class="agent-status-key">{axis.label}</span>
                  <span class="agent-status-value">{axis.valueLabel}</span>
                </button>
              }
            >
              <button
                type="button"
                class="agent-status-toggle"
                classList={{ on: isToggleOn(axis) }}
                aria-pressed={isToggleOn(axis)}
                title={`${axis.label}: ${axis.valueLabel} — click to toggle`}
                onClick={() => onToggle(axis)}
              >
                {toggleLabel(axis)}
              </button>
            </Show>
          )}
        </For>
        <AgentControlPicker backendId={props.backendId} slot={props.slot} />
      </div>
    </Show>
  );
}
