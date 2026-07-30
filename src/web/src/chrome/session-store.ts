import { createMemo, createSignal } from "solid-js";
import {
  activeBackendId,
  backendName,
  backendPhase,
  connectedBackends,
  currentEditorBinding,
  editorBackendId,
  editorRailSessionId,
  onBackendDisconnected,
  onBackendPhase,
  onSessionMessage,
  type SessionChip,
  type SessionStatusName,
} from "../bridge";
import { demoteSession, isPromoted, promotedKeys, promoteSession } from "./rail-state";
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
  /** The backend's link is down (socket opening/retrying) — the session can't be reached right now. */
  offline: boolean;
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
export interface SessionSwitchTarget {
  backendId: string;
  id: string;
}

const [switchIntent, setSwitchIntent] = createSignal<SessionSwitchTarget | null>(null);

/** The requested session whose file index must replace the mounted session's before navigation is re-enabled. */
export const sessionSwitchIntent = switchIntent;

// True once ANY backend has pushed its session-list — i.e. the host has answered `ready` with the initial
// session state. Distinguishes "no sessions yet, still booting" from "the host says there are none", which
// the reveal path needs: a launch that lands with zero loaded terminals (all-dormant restore, offline
// remote) must still bring the editor up rather than wait forever on a terminal frame that never comes.
const [sessionsReceived, setSessionsReceived] = createSignal(false);

export { sessionsReceived };

const normalizeProvider = (chip: SessionChip): SessionChip => ({
  ...chip,
  providerId: chip.providerId ?? "claude",
  agentSurface: chip.agentSurface ?? (chip.providerId === "codex" ? "structured" : "terminal"),
});

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

export const sameSessionSwitchTarget = (
  left: SessionSwitchTarget | null,
  right: SessionSwitchTarget | null,
): boolean =>
  left === null
    ? right === null
    : right !== null && left.backendId === right.backendId && left.id === right.id;

const cancelSwitch = (backendId: string): void => {
  if (switchIntent()?.backendId === backendId) {
    setSwitchIntent(null);
  }
};

onSessionMessage((message, backendId) => {
  if (message.type === "session-list") {
    setSessionsReceived(true);
    const intent = switchIntent();
    if (intent?.backendId === backendId) {
      const target = message.sessions.find((session) => session.id === intent.id);
      if (target === undefined) {
        cancelSwitch(backendId);
      }
    }
    setByBackend((prev) => {
      const next = new Map(prev);
      next.set(backendId, message.sessions.map(normalizeProvider));
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

onBackendDisconnected((backendId) => {
  setByBackend((prev) => {
    const next = new Map(prev);
    next.delete(backendId);
    return next;
  });
  cancelSwitch(backendId);
});

// A backend whose link dropped can no longer commit an in-flight switch (the frame is gone and offline
// frames are never buffered), so the optimistic highlight snaps back instead of sticking forever.
onBackendPhase((backendId, phase) => {
  if (phase !== "online") {
    cancelSwitch(backendId);
  }
});

// Every backend's chips, local first. A chip is active only when its backend is the one driving the page,
// so a background backend never shows a second highlighted chip.
const merged = createMemo<RailSession[]>(() => {
  const boundBackend = editorBackendId() ?? activeBackendId();
  const boundRailSession = editorRailSessionId();
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
        active:
          backendId === boundBackend &&
          (boundRailSession !== null ? chip.id === boundRailSession : chip.active),
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

/** Highlight and step from a requested target while the committed editor/backend projection is in flight. */
export function projectSessionSwitch(backendId: string, id: string): void {
  setSwitchIntent({ backendId, id });
}

/** The session whose editor projection currently owns session-scoped browser state. */
export function mountedSessionIndexOwner(): SessionSwitchTarget | null {
  const binding = currentEditorBinding();
  if (binding?.protocol === "projection") {
    return { backendId: binding.backendId, id: binding.railSessionId };
  }
  const backendId = binding?.backendId ?? activeBackendId();
  const active = (byBackend().get(backendId) ?? []).find((session) => session.active);
  return active === undefined ? null : { backendId, id: active.id };
}

/** Completes a switch only after its mounted target's first owned index has replaced the outgoing cache. */
export function completeSessionSwitchIndex(owner: SessionSwitchTarget): void {
  const intent = switchIntent();
  const mounted = mountedSessionIndexOwner();
  if (
    intent !== null &&
    sameSessionSwitchTarget(intent, owner) &&
    sameSessionSwitchTarget(mounted, owner)
  ) {
    setSwitchIntent(null);
  }
}

/** The rail's working set: every local session, plus promoted remotes (tagged with their agent hue). */
export const railSessions = createMemo<RailSession[]>(() => {
  // Read promotedKeys() so the memo re-runs when the promoted set changes (isPromoted reads it internally).
  void promotedKeys();
  const projected = switchIntent();
  return merged()
    .filter((s) => s.isLocal || isPromoted(s.backendId, s.id))
    .map((s) => ({
      ...(s.isLocal ? s : { ...s, agentHue: agentHue(s.locationName) }),
      active:
        projected === null
          ? s.active
          : s.backendId === projected.backendId && s.id === projected.id,
    }));
});

/**
 * The rail chip a next/prev step over `list` (LOADED chips only) should land on for `delta` (±1, wrapping), or
 * null when there's nothing to move to. With no active chip — e.g. deleting the focused session leaves the page
 * bound to a backend with no docked chip — any single chip is a valid recovery target (near end: first for next,
 * last for prev), so Ctrl+Tab / Ctrl+Shift+Tab recover focus instead of dead-keying.
 */
export function stepRailTarget(list: RailSession[], delta: number): RailSession | null {
  const current = list.findIndex((s) => s.active);
  if (list.length < (current < 0 ? 1 : 2)) {
    return null;
  }
  let index: number;
  if (current < 0) {
    index = delta < 0 ? list.length - 1 : 0;
  } else {
    index = (current + delta + list.length) % list.length;
  }
  return list[index] ?? null;
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

/** The active session's Claude status for the pane footer, or undefined until the first push. */
export const claudeStatus = status;

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
