// Per-session composer control state (model / mode / permissions / slash). Every live session receives its
// own controls stream; selection only chooses which owned state the status line renders.

import { createSignal } from "solid-js";
import {
  type AgentControlState,
  type AgentModelChoice,
  type ClientSession,
  registerSessionFeature,
} from "../bridge";

/** The reserved axis id for the merged model → effort / Fast control's cascading picker. */
export const MODEL_AXIS = "model";

const EMPTY: AgentControlState = {
  modelControl: { value: "", valueLabel: "", models: [] },
  axes: [],
  slash: [],
};
const [states, setStates] = createSignal(new Map<ClientSession, AgentControlState>());
// Which axis id's picker is open (null = none); the composer owns the one active picker at a time.
const [openAxis, setOpenAxis] = createSignal<string | null>(null);

/** One exact session's control surface; empty before its host has reported one. */
export function agentControlState(session: ClientSession | null): AgentControlState {
  return session === null || session.closed ? EMPTY : (states().get(session) ?? EMPTY);
}

/** The active model in a session's control state, or undefined before the host reports it. */
export function currentModel(session: ClientSession | null): AgentModelChoice | undefined {
  return agentControlState(session).modelControl.models.find((model) => model.current);
}

/** Sends a live provider-owned control change for a session to its host. */
export function setAgentControl(session: ClientSession, axis: string, value: string): void {
  if (!session.closed) {
    session.feature("agent").publish("setControl", { axis, value });
  }
}

/** Toggles the command-owned axis to its other provider-advertised option. */
export function toggleAgentControl(session: ClientSession, commandId: string): boolean {
  const axis = agentControlState(session).axes.find(
    (candidate) => candidate.commandId === commandId,
  );
  const target = axis?.options.find((option) => option.id !== axis.value);
  if (axis === undefined || target === undefined) {
    return false;
  }
  setAgentControl(session, axis.id, target.id);
  return true;
}

/** Switches to a model (its default effort applies on the host). */
export function selectModel(session: ClientSession, model: AgentModelChoice): void {
  setAgentControl(session, "model", model.id);
}

/** Selects a specific effort under a model, switching to that model first when it isn't current. */
export function selectModelEffort(
  session: ClientSession,
  model: AgentModelChoice,
  effortId: string,
): void {
  if (!model.current) {
    setAgentControl(session, "model", model.id);
  }
  setAgentControl(session, "effort", effortId);
}

/** Toggles Fast Mode for a model, switching to that model first when it isn't current. */
export function toggleModelFast(session: ClientSession, model: AgentModelChoice): void {
  if (model.fastTier === "") {
    return;
  }
  if (!model.current) {
    setAgentControl(session, "model", model.id);
  }
  setAgentControl(session, "serviceTier", model.fastOn ? "standard" : model.fastTier);
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

registerSessionFeature((session) => {
  const off = session.feature("agent").on<{ state: AgentControlState }>("controls", ({ state }) => {
    setStates((previous) => new Map(previous).set(session, state));
  });
  return () => {
    off();
    setStates((previous) => {
      const next = new Map(previous);
      next.delete(session);
      return next;
    });
  };
});
