import { createMemo, createSignal } from "solid-js";
import {
  backendName,
  backendPhase,
  type ClientSession,
  connectedBackends,
  onBackendDisconnected,
  onBackendPhase,
  onSelectedSession,
  registerHostFeature,
  registerSessionFeature,
  type SessionChip,
  type SessionStatusName,
  selectedSession,
} from "../bridge";
import { demoteSession, isPromoted, promotedKeys, promoteSession } from "./rail-state";
import { agentBackendId, agentHue, remoteAgents } from "./remote-agents";

// Re-export the promote/demote/isPromoted API so consumers reach it through the session store; the state
// itself lives host-side in rail-state.ts (persisted, not in localStorage).
export { demoteSession, isPromoted, promoteSession };

// The rail's working set is every local session plus promoted remotes. Each connection publishes its catalog,
// kept by host while each loaded entry points at an exact ClientSession.

/** One rail chip plus which backend (location) it lives on. */
export interface RailSession extends SessionChip {
  /** The exact live owner; null for a dormant catalog slot. */
  owner: ClientSession | null;
  backendId: string;
  /** The backend's display name ("default" for local, else the registered agent name). */
  locationName: string;
  isLocal: boolean;
  /** The agent's identity hue (remote sessions only), colouring the remote marker at rest. */
  agentHue?: number;
  /** A host op (delete / load / unload) is in flight against this session — its chip shows a spinner. */
  pending: boolean;
  /** The backend's link is down (socket opening/retrying) — the session can't be reached right now. */
  offline: boolean;
  /** Whether this exact client session is selected. */
  active: boolean;
}

/** A remote agent and its sessions, for the cloud panel. Offline = registered but not currently connected. */
export interface RemoteAgentRow {
  backendId: string;
  name: string;
  hue: number;
  connected: boolean;
  sessions: RailSession[];
}

interface BackendSession extends SessionChip {
  owner: ClientSession | null;
}

const [byBackend, setByBackend] = createSignal<Map<string, BackendSession[]>>(new Map());
const [pendingSelection, setPendingSelection] = createSignal<{
  backendId: string;
  id: string;
} | null>(null);

// True once any host has supplied its initial catalog. Distinguishes "no sessions yet, still booting" from
// "the host says there are none", which
// the reveal path needs: a launch that lands with zero loaded terminals (all-dormant restore, offline
// remote) must still bring the editor up rather than wait forever on a terminal frame that never comes.
const [sessionsReceived, setSessionsReceived] = createSignal(false);

export { sessionsReceived };

// Sessions with a host op (delete / load / unload) in flight, refcounted by `${backendId}:${id}` so
// overlapping ops don't clear the spinner early. The chip shows a spinner while its count is positive.
const [pendingSessions, setPendingSessions] = createSignal<Map<string, number>>(new Map());
const pendingKey = (backendId: string, id: string): string => `${backendId}:${id}`;
const adjustPending = (key: string, delta: number): void => {
  setPendingSessions((prev) => {
    const next = new Map(prev);
    const count = (next.get(key) ?? 0) + delta;
    if (count > 0) {
      next.set(key, count);
    } else {
      next.delete(key);
    }
    return next;
  });
};

/** Flag a session as pending (spinner on its chip) for the duration of a host command, cleared when it settles. */
export function trackSessionCommand<T>(
  backendId: string,
  id: string,
  run: () => Promise<T>,
): Promise<T> {
  const key = pendingKey(backendId, id);
  adjustPending(key, 1);
  return run().finally(() => adjustPending(key, -1));
}

registerHostFeature((connection) =>
  connection.onCatalog((catalog) => {
    setSessionsReceived(true);
    const pending = pendingSelection();
    if (
      pending?.backendId === connection.id &&
      !catalog.some((session) => session.id === pending.id)
    ) {
      setPendingSelection(null);
    }
    setByBackend((prev) => {
      const next = new Map(prev);
      next.set(
        connection.id,
        catalog.map((entry) => ({
          owner: entry.address === null ? null : (connection.session(entry.address) ?? null),
          id: entry.id,
          label: entry.label,
          loaded: entry.loaded,
          providerId: entry.providerId,
          agentSurface: entry.agentSurface,
          agentInputProtocol: entry.agentInputProtocol,
          status: entry.status,
          hue: entry.hue,
          monogram: entry.monogram,
        })),
      );
      return next;
    });
  }),
);

registerSessionFeature((session) =>
  session.feature("status").on<{ status: SessionStatusName }>("changed", ({ status }) => {
    setByBackend((previous) => {
      const chips = previous.get(session.connection.id);
      if (chips === undefined) {
        return previous;
      }
      const next = new Map(previous);
      next.set(
        session.connection.id,
        chips.map((chip) => (chip.owner === session ? { ...chip, status } : chip)),
      );
      return next;
    });
  }),
);

onBackendDisconnected((backendId) => {
  setByBackend((prev) => {
    const next = new Map(prev);
    next.delete(backendId);
    return next;
  });
  if (pendingSelection()?.backendId === backendId) {
    setPendingSelection(null);
  }
});

// A backend whose link dropped can no longer commit an in-flight switch (the frame is gone and offline
// frames are never buffered), so the optimistic highlight snaps back instead of sticking forever.
onBackendPhase((backendId, phase) => {
  if (phase !== "online" && pendingSelection()?.backendId === backendId) {
    setPendingSelection(null);
  }
});

onSelectedSession(() => setPendingSelection(null));

// Every backend's chips, local first. A chip is active only when its backend is the one driving the page,
// so a background backend never shows a second highlighted chip.
const merged = createMemo<RailSession[]>(() => {
  const selected = selectedSession();
  const pending = pendingSessions();
  // Only still-connected backends, so a disconnected remote's lingering chips leave the rail immediately.
  const connected = new Set(connectedBackends().map((b) => b.id));
  const out: RailSession[] = [];
  for (const [backendId, chips] of byBackend()) {
    if (!connected.has(backendId)) {
      continue;
    }
    const isLocal = backendId === "local";
    const offline = backendPhase(backendId) !== "online";
    for (const chip of chips) {
      out.push({
        ...chip,
        backendId,
        isLocal,
        locationName: backendName(backendId),
        // Dormant slots have no owner, so guard the null case: with nothing selected they'd all read active.
        active: chip.owner !== null && selected === chip.owner,
        pending: pending.has(pendingKey(backendId, chip.id)),
        offline,
      });
    }
  }
  return out;
});

/** The merged sessions across all connected backends (local + remotes). Drives terminals + the cloud panel. */
export const sessions = merged;

/** The session with `id` on `backendId`, or undefined when no connected backend carries it. */
export function findSession(backendId: string, id: string): RailSession | undefined {
  return merged().find((s) => s.backendId === backendId && s.id === id);
}

/**
 * Highlights a requested target while dirty editor state is flushed before selection commits. The returned
 * dispose drops that highlight once the switch settles — a switch that never commits (superseded, failed)
 * would otherwise leave the rail, and the cycling that steps from it, pointing at a session nobody is on.
 */
export function beginSessionSelection(backendId: string, id: string): () => void {
  const intent = { backendId, id };
  setPendingSelection(intent);
  return () => {
    if (pendingSelection() === intent) {
      setPendingSelection(null);
    }
  };
}

/** The rail's working set: every local session, plus promoted remotes (tagged with their agent hue). */
export const railSessions = createMemo<RailSession[]>(() => {
  // Read promotedKeys() so the memo re-runs when the promoted set changes (isPromoted reads it internally).
  void promotedKeys();
  const pending = pendingSelection();
  return merged()
    .filter((s) => s.isLocal || isPromoted(s.backendId, s.id))
    .map((s) => ({
      ...(s.isLocal ? s : { ...s, agentHue: agentHue(s.locationName) }),
      active:
        pending === null ? s.active : s.backendId === pending.backendId && s.id === pending.id,
    }));
});

/**
 * The rail chip a next/prev step over `list` (LOADED chips only) should land on for `delta` (±1, wrapping), or
 * null when there's nothing to move to. A step originates at the highlighted chip, else at the session on
 * screen — so a switch still in flight to a chip off this list (a dormant one loading) can't make the step a
 * no-op or send it backwards. With neither here — deleting the focused session leaves the page bound to a
 * backend with no docked chip — it recovers to the near end (first for next, last for prev).
 */
export function stepRailTarget(list: RailSession[], delta: number): RailSession | null {
  const onScreen = selectedSession();
  const active = list.findIndex((s) => s.active);
  const from =
    active >= 0 || onScreen === null ? active : list.findIndex((s) => s.owner === onScreen);
  if (from >= 0) {
    return list.length < 2 ? null : (list[(from + delta + list.length) % list.length] ?? null);
  }
  return (delta < 0 ? list[list.length - 1] : list[0]) ?? null;
}

/** Every registered remote agent and its sessions, for the cloud panel (connected first, offline faded). */
export const remoteAgentRows = createMemo<RemoteAgentRow[]>(() => {
  const remotes = connectedBackends().filter((b) => !b.isLocal);
  const connectedNames = new Set(remotes.map((b) => b.name));
  const online: RemoteAgentRow[] = remotes.map((b) => ({
    backendId: b.id,
    name: b.name,
    hue: agentHue(b.name),
    connected: true,
    sessions: merged()
      .filter((s) => s.backendId === b.id)
      .map((s) => ({ ...s, agentHue: agentHue(b.name) })),
  }));
  const offline: RemoteAgentRow[] = remoteAgents()
    .filter((a) => !connectedNames.has(a.name))
    .map((a) => ({
      backendId: agentBackendId(a.name),
      name: a.name,
      hue: agentHue(a.name),
      connected: false,
      sessions: [],
    }));
  return [...online, ...offline];
});

/** Whether any remote session is mid-turn, awaiting input, or waiting on a task — flags the cloud button so off-rail work is visible. */
export const remoteActivity = createMemo<boolean>(() =>
  merged().some(
    (s) =>
      !s.isLocal && (s.status === "working" || s.status === "needsInput" || s.status === "waiting"),
  ),
);

/** The selected session's agent status for the pane footer, or undefined until the first push. */
export const claudeStatus = createMemo(() => sessions().find((session) => session.active)?.status);

/** Full tooltip for each Claude status (the footer segment's `title`). */
export const STATUS_LABEL: Record<SessionStatusName, string> = {
  starting: "Claude is starting",
  working: "Claude is working",
  needsInput: "Claude needs your input",
  idle: "Claude is idle",
  waiting: "Claude is waiting on a scheduled task",
  error: "Claude crashed",
};

/** Compact label for each Claude status (the footer segment's visible text). */
export const STATUS_SHORT: Record<SessionStatusName, string> = {
  starting: "Starting",
  working: "Working",
  needsInput: "Needs input",
  idle: "Idle",
  waiting: "Waiting",
  error: "Crashed",
};
