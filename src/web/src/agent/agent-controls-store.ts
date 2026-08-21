// Per-session composer control state (model / mode / permissions / slash). Every live session receives its
// own controls stream; selection only chooses which owned state the status line renders.

import { createSignal } from "solid-js";
import type { AgentControlState, ClientSession } from "../bridge";
import { createSessionFeatureValue } from "../messaging/session-feature-value";

const EMPTY: AgentControlState = {
  axes: [],
  slash: [],
};
const stateFor = createSessionFeatureValue<{ state: AgentControlState }, AgentControlState>(
  "agent",
  "controls",
  ({ state }) => state,
);
// Which axis id's picker is open (null = none); the composer owns the one active picker at a time.
const [openAxis, setOpenAxis] = createSignal<string | null>(null);

/** One exact session's control surface; empty before its host has reported one. */
export function agentControlState(session: ClientSession | null): AgentControlState {
  return stateFor(session) ?? EMPTY;
}

/** Sends a live provider-owned control change for a session to its host. */
export function setAgentControl(session: ClientSession, axis: string, value: string): void {
  if (!session.closed) {
    session.feature("agent").publish("setControl", { axis, value });
  }
}

/** The axis whose picker is currently open, or null. */
export function openControlAxis(): string | null {
  return openAxis();
}

/** Opens the picker for an axis (from a `/model`-style command). */
export function openControlPicker(axis: string): void {
  setOpenAxis(axis);
}

/** Toggles an axis's picker: a status-line segment closes the picker it opened, and switches axes otherwise. */
export function toggleControlPicker(axis: string): void {
  setOpenAxis((current) => (current === axis ? null : axis));
}

/** Closes any open control picker. */
export function closeControlPicker(): void {
  setOpenAxis(null);
}
