import { type ClientSession, registerSessionFeature } from "../bridge";
import type { AgentControlOption } from "../messaging/protocol-types";
import { createSessionOwnedState } from "../messaging/session-owned-state";

type HostAsk<T> = T & { session: ClientSession };

// A host's ask of one session's page: shown while that session is on screen, and answered once, on that session.
function hostAsk<T extends object>(feature: string, event: string) {
  const asks = createSessionOwnedState<HostAsk<T> | null>(() => null);
  registerSessionFeature((session) =>
    session
      .feature(feature)
      .on<T>(event, (ask) => asks.update(session, () => ({ session, ...ask }))),
  );
  return {
    of: (session: ClientSession | null): HostAsk<T> | null => asks.get(session) ?? null,
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

export const credentialPromptOf = prompts.of;
export const dismissCredentialPrompt = (session: ClientSession): void =>
  prompts.answer(session, "dismiss", {});
export const submitCredential = (
  session: ClientSession,
  id: string,
  key: string,
): Promise<{ ok: boolean; error: string }> =>
  session.feature("credentials").request("save", { id, key });

export const choicePickerOf = pickers.of;
export const pickChoice = (session: ClientSession, id: string): void =>
  pickers.answer(session, "pick", { id });
export const dismissChoicePicker = (session: ClientSession): void =>
  pickers.answer(session, "dismiss", {});
