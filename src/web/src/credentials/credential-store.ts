import { type ClientSession, registerSessionFeature, selectedSession } from "../bridge";
import { createSessionOwnedState } from "../messaging/session-owned-state";

/** The host's ask for a service key, owned by the session that asked. */
export interface CredentialPrompt {
  session: ClientSession;
  id: string;
  displayName: string;
  keyNoun: string;
  keyPageUrl: string;
}

const prompts = createSessionOwnedState<CredentialPrompt | null>(() => null);

export function selectedCredentialPrompt(): CredentialPrompt | null {
  return prompts.get(selectedSession()) ?? null;
}

export function dismissCredentialPrompt(session: ClientSession): void {
  prompts.update(session, () => null);
  session.feature("credentials").publish("dismiss", {});
}

export function submitCredential(
  session: ClientSession,
  id: string,
  key: string,
): Promise<{ ok: boolean; error: string }> {
  return session.feature("credentials").request("save", { id, key });
}

registerSessionFeature((session) =>
  session
    .feature("credentials")
    .on<Omit<CredentialPrompt, "session">>("prompt", (prompt) =>
      prompts.update(session, () => ({ session, ...prompt })),
    ),
);
