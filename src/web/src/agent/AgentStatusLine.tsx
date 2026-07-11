import { createEffect, For, type JSX, Show } from "solid-js";
import { AgentControlPicker } from "./AgentControlPicker";
import { agentControlState, closeControlPicker, openControlPicker } from "./agent-controls-store";

// The dim strip under the composer showing the session's model / approvals / sandbox. Each segment opens its
// picker; the values are provider-neutral — the web never learns they came from Codex. Generic over axes, so a
// provider that reports different controls renders without a code change here.
export function AgentStatusLine(props: { backendId: string; slot: string | null }): JSX.Element {
  const state = (): ReturnType<typeof agentControlState> => agentControlState(props.slot);
  // Switching sessions abandons an open picker so it can't apply to the wrong session.
  createEffect(() => {
    props.slot;
    closeControlPicker();
  });

  return (
    <Show when={state().axes.length > 0}>
      <div class="agent-status-line">
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
        <AgentControlPicker backendId={props.backendId} slot={props.slot} />
      </div>
    </Show>
  );
}
