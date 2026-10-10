import { type JSX, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { ControlMenu } from "../chrome/ControlMenu";
import {
  agentControlState,
  closeControlPicker,
  openControlAxis,
  setAgentControl,
} from "./agent-controls-store";

// The popover that opens above a status-line segment (or from a `/model`-style command). AgentStatusLine owns the
// `agentControlPickerOpen` gate (whenever any picker is open) so the composer's Enter/Escape commands stand down.
export function AgentControlPicker(props: { session: ClientSession | null }): JSX.Element {
  // Keyed on the axis id alone: a host re-push rebuilds the axes, which must not reset keyboard navigation mid-use.
  const axisId = () => (props.session === null ? null : openControlAxis());
  return (
    <Show when={axisId()} keyed>
      {(id) => (
        <Show
          when={
            props.session !== null &&
            agentControlState(props.session).axes.find((candidate) => candidate.id === id)
          }
        >
          {(axis) => (
            <ControlMenu
              axis={axis()}
              class="agent-control-picker"
              inside=".agent-control-picker, .agent-status-axis"
              onPick={(optionId) => {
                if (props.session !== null) setAgentControl(props.session, id, optionId);
                closeControlPicker();
              }}
              onClose={closeControlPicker}
            />
          )}
        </Show>
      )}
    </Show>
  );
}
