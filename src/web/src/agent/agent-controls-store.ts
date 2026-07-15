// Per-slot composer control state (model / mode / permissions / slash), pushed by the host as `agent-controls`
// and echoed back as `agent-set-control`. Keyed by slot like the agent pane: the active-backend gate means only
// the active backend's slots arrive, and the host re-pushes on reconnect/switch, so the status line self-heals.

import { createSignal } from "solid-js";
import {
  type AgentControlState,
  type AgentModelChoice,
  onHostMessage,
  postToBackend,
} from "../bridge";

/** The reserved axis id for the merged model → effort / Fast control's cascading picker. */
export const MODEL_AXIS = "model";

const EMPTY: AgentControlState = {
  modelControl: { value: "", valueLabel: "", models: [] },
  axes: [],
  slash: [],
};
const [states, setStates] = createSignal<Record<string, AgentControlState>>({});
// Which axis id's picker is open (null = none); the composer owns the one active picker at a time.
const [openAxis, setOpenAxis] = createSignal<string | null>(null);

/** The control surface for a slot; an empty surface before the host has reported one. */
export function agentControlState(slot: string | null): AgentControlState {
  return slot === null ? EMPTY : (states()[slot] ?? EMPTY);
}

/** The active model in a slot's control state, or undefined before the catalog loads. */
export function currentModel(slot: string | null): AgentModelChoice | undefined {
  return agentControlState(slot).modelControl.models.find((model) => model.current);
}

/** Sends a live provider-owned control change for a session to its host. */
export function setAgentControl(
  backendId: string,
  slot: string,
  axis: string,
  value: string,
): void {
  postToBackend(backendId, { type: "agent-set-control", slot, axis, value });
}

/** Toggles the command-owned axis to its other provider-advertised option. */
export function toggleAgentControl(backendId: string, slot: string, commandId: string): boolean {
  const axis = agentControlState(slot).axes.find((candidate) => candidate.commandId === commandId);
  const target = axis?.options.find((option) => option.id !== axis.value);
  if (axis === undefined || target === undefined) {
    return false;
  }
  setAgentControl(backendId, slot, axis.id, target.id);
  return true;
}

/** Switches to a model (its default effort applies on the host). */
export function selectModel(backendId: string, slot: string, model: AgentModelChoice): void {
  setAgentControl(backendId, slot, "model", model.id);
}

/** Selects a specific effort under a model, switching to that model first when it isn't current. */
export function selectModelEffort(
  backendId: string,
  slot: string,
  model: AgentModelChoice,
  effortId: string,
): void {
  if (!model.current) {
    setAgentControl(backendId, slot, "model", model.id);
  }
  setAgentControl(backendId, slot, "effort", effortId);
}

/** Toggles Fast Mode for a model, switching to that model first when it isn't current. */
export function toggleModelFast(backendId: string, slot: string, model: AgentModelChoice): void {
  if (model.fastTier === "") {
    return;
  }
  if (!model.current) {
    setAgentControl(backendId, slot, "model", model.id);
  }
  setAgentControl(backendId, slot, "serviceTier", model.fastOn ? "standard" : model.fastTier);
}

/** The axis whose picker is currently open, or null. */
export function openControlAxis(): string | null {
  return openAxis();
}

/** Opens the picker for an axis (from a status-line segment or a `/model`-style command). */
export function openControlPicker(axis: string): void {
  setOpenAxis(axis);
}

/** Closes any open control picker. */
export function closeControlPicker(): void {
  setOpenAxis(null);
}

onHostMessage((message) => {
  if (message.type === "agent-controls") {
    setStates((prev) => ({ ...prev, [message.slot]: message.state }));
  }
});
