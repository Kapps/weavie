import { createSignal } from "solid-js";
import { type ClientSession, registerSessionFeature } from "../bridge";

export interface ShellTerminalDescriptor {
  id: string;
}

interface ShellTerminalState {
  terminals: ShellTerminalDescriptor[];
  activeId: string | null;
}

const [states, setStates] = createSignal(new Map<ClientSession, ShellTerminalState>());

function applyCatalog(session: ClientSession, terminals: ShellTerminalDescriptor[]): void {
  if (
    terminals.some((terminal) => terminal.id.length === 0) ||
    new Set(terminals.map((terminal) => terminal.id)).size !== terminals.length
  ) {
    throw new Error("The shell terminal catalog contains an invalid or duplicate id.");
  }
  const previous = states().get(session);
  const previousActive = previous?.activeId ?? null;
  let activeId = previousActive;
  if (activeId === null || !terminals.some((terminal) => terminal.id === activeId)) {
    const oldIndex =
      previous?.terminals.findIndex((terminal) => terminal.id === previousActive) ?? -1;
    activeId = terminals[Math.min(Math.max(oldIndex, 0), terminals.length - 1)]?.id ?? null;
  }
  setStates((current) => {
    const next = new Map(current);
    next.set(session, { terminals, activeId });
    return next;
  });
}

registerSessionFeature((session) => {
  const offCatalog = session
    .feature("terminal.shell")
    .on<{ terminals: ShellTerminalDescriptor[] }>("catalog", ({ terminals }) =>
      applyCatalog(session, terminals),
    );
  return () => {
    offCatalog();
    setStates((current) => {
      const next = new Map(current);
      next.delete(session);
      return next;
    });
  };
});

export function shellTerminals(session: ClientSession | null): ShellTerminalDescriptor[] {
  return session === null ? [] : (states().get(session)?.terminals ?? []);
}

export function shellTerminalCatalogReceived(session: ClientSession | null): boolean {
  return session !== null && states().has(session);
}

export function activeShellTerminalId(session: ClientSession | null): string | null {
  return session === null ? null : (states().get(session)?.activeId ?? null);
}

export function selectShellTerminal(session: ClientSession, id: string): boolean {
  const current = states().get(session);
  if (current === undefined || !current.terminals.some((terminal) => terminal.id === id)) {
    return false;
  }
  if (current.activeId === id) {
    return true;
  }
  setStates((states) => {
    const next = new Map(states);
    next.set(session, { ...current, activeId: id });
    return next;
  });
  return true;
}

export function stepShellTerminal(session: ClientSession, delta: -1 | 1): boolean {
  const state = states().get(session);
  if (state === undefined || state.terminals.length < 2) {
    return false;
  }
  const index = state.terminals.findIndex((terminal) => terminal.id === state.activeId);
  const next = (index + delta + state.terminals.length) % state.terminals.length;
  return selectShellTerminal(session, state.terminals[next]?.id ?? "");
}
