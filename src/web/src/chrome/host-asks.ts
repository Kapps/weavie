import { type ClientSession, registerSessionFeature, selectedSession } from "../bridge";
import type { AgentControlOption } from "../messaging/protocol-types";
import { createSessionOwnedState } from "../messaging/session-owned-state";

type HostAsk<T> = T & { session: ClientSession };

// A host's ask of one session's page: shown while that session is selected, and answered once, on that session.
function hostAsk<T extends object>(feature: string, event: string) {
  const asks = createSessionOwnedState<HostAsk<T> | null>(() => null);
  registerSessionFeature((session) =>
    session
      .feature(feature)
      .on<T>(event, (ask) => asks.update(session, () => ({ session, ...ask }))),
  );
  return {
    selected: (): HostAsk<T> | null => asks.get(selectedSession()) ?? null,
    answer(session: ClientSession, name: string, payload: object): void {
      if (!asks.get(session)) return;
      asks.update(session, () => null);
      session.feature(feature).publish(name, payload);
    },
  };
}

/** The host's ask for a service key. */
export type CredentialPrompt = HostAsk<{
  id: string;
  displayName: string;
  keyNoun: string;
  keyPageUrl: string;
}>;

/** A host-driven choice. */
export type ChoicePickerRequest = HostAsk<{
  title: string;
  value: string;
  options: AgentControlOption[];
}>;

const prompts = hostAsk<Omit<CredentialPrompt, "session">>("credentials", "prompt");
const pickers = hostAsk<Omit<ChoicePickerRequest, "session">>("picker", "show");

export const selectedCredentialPrompt = prompts.selected;
export const dismissCredentialPrompt = (session: ClientSession): void =>
  prompts.answer(session, "dismiss", {});
export const submitCredential = (
  session: ClientSession,
  id: string,
  key: string,
): Promise<{ ok: boolean; error: string }> =>
  session.feature("credentials").request("save", { id, key });

export const selectedChoicePicker = pickers.selected;
export const pickChoice = (session: ClientSession, id: string): void =>
  pickers.answer(session, "pick", { id });
export const dismissChoicePicker = (session: ClientSession): void =>
  pickers.answer(session, "dismiss", {});
