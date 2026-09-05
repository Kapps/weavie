import type { ClientSession } from "../bridge";
import { createSessionOwnedMap } from "../messaging/session-owned-state";

export interface AsideReplyState {
  draft: string;
  open: boolean;
}

const EMPTY: AsideReplyState = { draft: "", open: false };
const states = createSessionOwnedMap<string, AsideReplyState>();

export function asideReplyState(session: ClientSession, conversationId: string): AsideReplyState {
  return states.get(session, conversationId) ?? EMPTY;
}

export function setAsideReplyState(
  session: ClientSession,
  conversationId: string,
  state: AsideReplyState,
): void {
  if (!state.open && state.draft.length === 0) {
    clearAsideReplyState(session, conversationId);
    return;
  }
  states.set(session, conversationId, state);
}

export function clearAsideReplyState(session: ClientSession, conversationId: string): void {
  states.delete(session, conversationId);
}

export function clearAsideReplyStates(session: ClientSession): void {
  states.clear(session);
}
