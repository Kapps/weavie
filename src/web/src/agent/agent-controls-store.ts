// Per-slot composer control state (model / approvals / sandbox / slash), pushed by the host as `agent-controls`
// and echoed back as `agent-set-control`. Keyed by slot like the agent pane: the active-backend gate means only
// the active backend's slots arrive, and the host re-pushes on reconnect/switch, so the status line self-heals.

import { createSignal } from "solid-js";
import { type AgentControlState, onHostMessage, postToBackend } from "../bridge";

const EMPTY: AgentControlState = { axes: [], slash: [] };
const [states, setStates] = createSignal<Record<string, AgentControlState>>({});
// Which axis id's picker is open (null = none); the composer owns the one active picker at a time.
const [openAxis, setOpenAxis] = createSignal<string | null>(null);

/** The control surface for a slot; an empty surface before the host has reported one. */
export function agentControlState(slot: string | null): AgentControlState {
  return slot === null ? EMPTY : (states()[slot] ?? EMPTY);
}

/** Sends a live control change (model / approvals / sandbox) for a session to its host. */
export function setAgentControl(
  backendId: string,
  slot: string,
  axis: string,
  value: string,
): void {
  postToBackend(backendId, { type: "agent-set-control", slot, axis, value });
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
