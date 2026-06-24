import { createMemo, createSignal } from "solid-js";
import {
  type SessionChip,
  type SessionStatusName,
  activeBackendId,
  backendName,
  connectedBackends,
  onSessionMessage,
} from "../bridge";
import { demoteSession, isPromoted, promoteSession, promotedKeys } from "./rail-state";
import { agentBackendId, agentHue, remoteAgents } from "./remote-agents";

// Re-export the promote/demote/isPromoted API so consumers reach it through the session store; the state
// itself lives host-side in rail-state.ts (persisted, not in localStorage).
export { demoteSession, isPromoted, promoteSession };

// The rail's working set is every local session plus promoted remotes. Each backend pushes its own
// session-list, kept keyed by backend. Top-level module signals so they survive HMR.

/** One rail chip plus which backend (location) it lives on. */
export interface RailSession extends SessionChip {
  backendId: string;
  /** The backend's display name ("default" for local, else the registered agent name). */
  locationName: string;
  isLocal: boolean;
  /** The agent's identity hue (remote sessions only), colouring the remote marker at rest. */
  agentHue?: number;
  /** A host op (delete / load / unload) is in flight against this session — its chip shows a spinner. */
  pending: boolean;
}

/** A remote agent and its sessions, for the cloud panel. Offline = registered but not currently connected. */
export interface RemoteAgentRow {
  backendId: string;
  name: string;
  hue: number;
  connected: boolean;
  sessions: RailSession[];
}

const [byBackend, setByBackend] = createSignal<Map<string, SessionChip[]>>(new Map());
const [status, setStatus] = createSignal<SessionStatusName | undefined>(undefined);

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

onSessionMessage((message, backendId) => {
  if (message.type === "session-list") {
    setByBackend((prev) => {
      const next = new Map(prev);
      next.set(backendId, message.sessions);
      return next;
    });
  } else if (
    message.type === "session-status" &&
    message.session === "claude" &&
    backendId === activeBackendId()
  ) {
    // Only the active backend's claude drives the pane-head status dot.
    setStatus(message.status);
  }
});

// Every backend's chips, local first. A chip is active only when its backend is the one driving the page,
// so a background backend never shows a second highlighted chip.
const merged = createMemo<RailSession[]>(() => {
  const active = activeBackendId();
  const pending = pendingSessions();
  // Only still-connected backends, so a disconnected remote's lingering chips leave the rail immediately.
  const connected = new Set(connectedBackends().map((b) => b.id));
  const out: RailSession[] = [];
  for (const [backendId, chips] of byBackend()) {
    if (!connected.has(backendId)) {
      continue;
    }
    const isLocal = backendId === "local";
    for (const chip of chips) {
      out.push({
        ...chip,
        backendId,
        isLocal,
        locationName: backendName(backendId),
        active: chip.active && backendId === active,
        pending: pending.has(pendingKey(backendId, chip.id)),
      });
    }
  }
  return out;
});

/** The merged sessions across all connected backends (local + remotes). Drives terminals + the cloud panel. */
export const sessions = merged;

/** The rail's working set: every local session, plus promoted remotes (tagged with their agent hue). */
export const railSessions = createMemo<RailSession[]>(() => {
  // Read promotedKeys() so the memo re-runs when the promoted set changes (isPromoted reads it internally).
  void promotedKeys();
  return merged()
    .filter((s) => s.isLocal || isPromoted(s.backendId, s.id))
    .map((s) => (s.isLocal ? s : { ...s, agentHue: agentHue(s.locationName) }));
});

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

/** Whether any remote session is mid-turn or awaiting input — flags the cloud button so off-rail work is visible. */
export const remoteActivity = createMemo<boolean>(() =>
  merged().some((s) => !s.isLocal && (s.status === "working" || s.status === "needsInput")),
);

/** The active session's Claude status for the pane-head dot, or undefined until the first push. */
export const claudeStatus = status;
