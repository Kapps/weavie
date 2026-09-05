import { type ClientSession, registerSessionFeature } from "../bridge";
import { createSessionOwnedState } from "../messaging/session-owned-state";

interface ShellTerminalState {
  terminals: string[];
  activeId: string | null;
}

const states = createSessionOwnedState<ShellTerminalState | null>(() => null);

function applyCatalog(session: ClientSession, terminals: string[]): void {
  if (terminals.some((id) => id.length === 0) || new Set(terminals).size !== terminals.length) {
    throw new Error("The shell terminal catalog contains an invalid or duplicate id.");
  }
  const previous = states.get(session);
  const previousActive = previous?.activeId ?? null;
  let activeId = previousActive;
  if (activeId === null || !terminals.includes(activeId)) {
    const oldIndex = previous?.terminals.indexOf(previousActive ?? "") ?? -1;
    activeId = terminals[Math.min(Math.max(oldIndex, 0), terminals.length - 1)] ?? null;
  }
  states.update(session, () => ({ terminals, activeId }));
}

registerSessionFeature((session) => {
  const offCatalog = session
    .feature("terminal.shell")
    .on<{ terminalIds: string[] }>("catalog", ({ terminalIds }) =>
      applyCatalog(session, terminalIds),
    );
  return offCatalog;
});

export function shellTerminals(session: ClientSession | null): string[] {
  return states.get(session)?.terminals ?? [];
}

export function shellTerminalCatalogReceived(session: ClientSession | null): boolean {
  return states.get(session) !== null && states.get(session) !== undefined;
}

export function activeShellTerminalId(session: ClientSession | null): string | null {
  return states.get(session)?.activeId ?? null;
}

export function selectShellTerminal(session: ClientSession, id: string): boolean {
  const current = states.get(session);
  if (current == null || !current.terminals.includes(id)) {
    return false;
  }
  if (current.activeId === id) {
    return true;
  }
  states.update(session, () => ({ ...current, activeId: id }));
  return true;
}

export function stepShellTerminal(session: ClientSession, delta: -1 | 1): boolean {
  const state = states.get(session);
  if (state == null || state.terminals.length < 2) {
    return false;
  }
  const index = state.terminals.indexOf(state.activeId ?? "");
  const next = (index + delta + state.terminals.length) % state.terminals.length;
  return selectShellTerminal(session, state.terminals[next] ?? "");
}
