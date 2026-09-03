import type { ClientSession } from "../bridge";

export interface AsideReplyState {
  draft: string;
  open: boolean;
}

const EMPTY: AsideReplyState = { draft: "", open: false };
const states = new WeakMap<ClientSession, Map<string, AsideReplyState>>();

export function asideReplyState(session: ClientSession, conversationId: string): AsideReplyState {
  return states.get(session)?.get(conversationId) ?? EMPTY;
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
  const sessionStates = states.get(session);
  if (sessionStates === undefined) {
    states.set(session, new Map([[conversationId, state]]));
  } else {
    sessionStates.set(conversationId, state);
  }
}

export function clearAsideReplyState(session: ClientSession, conversationId: string): void {
  const sessionStates = states.get(session);
  sessionStates?.delete(conversationId);
  if (sessionStates?.size === 0) states.delete(session);
}

export function clearAsideReplyStates(session: ClientSession): void {
  states.delete(session);
}
