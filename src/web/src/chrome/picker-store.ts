import { type ClientSession, registerSessionFeature, selectedSession } from "../bridge";
import type { AgentControlOption } from "../messaging/protocol-types";
import { createSessionOwnedState } from "../messaging/session-owned-state";

/** A host-driven choice, answered on the session that asked for it. */
export interface ChoicePickerRequest {
  session: ClientSession;
  title: string;
  value: string;
  options: AgentControlOption[];
}

const pickers = createSessionOwnedState<ChoicePickerRequest | null>(() => null);

export function selectedChoicePicker(): ChoicePickerRequest | null {
  return pickers.get(selectedSession()) ?? null;
}

function answer(session: ClientSession, name: "pick" | "dismiss", payload: object): void {
  if (!pickers.get(session)) return;
  pickers.update(session, () => null);
  session.feature("picker").publish(name, payload);
}

export const pickChoice = (session: ClientSession, id: string): void =>
  answer(session, "pick", { id });

export const dismissChoicePicker = (session: ClientSession): void => answer(session, "dismiss", {});

registerSessionFeature((session) =>
  session
    .feature("picker")
    .on<Omit<ChoicePickerRequest, "session">>("show", (picker) =>
      pickers.update(session, () => ({ session, ...picker })),
    ),
);
