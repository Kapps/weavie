import { createMemo, createSignal } from "solid-js";
import {
  type SessionChip,
  type SessionStatusName,
  activeBackendId,
  backendName,
  onSessionMessage,
} from "../bridge";

// The session rail aggregates sessions from EVERY connected backend (the local/default host plus each
// registered remote agent), so local and remote sessions sit in one rail. Each backend pushes its own
// session-list; we keep them keyed by backend and render the union, tagging each chip with its location.
// Like layout/store.ts these are TOP-LEVEL module signals (seeded by host pushes, surviving HMR) — see the
// note in the original store; a component signal would blank the rail on every hot reload.

/** One rail chip plus which backend (location) it lives on. */
export interface RailSession extends SessionChip {
  backendId: string;
  /** The backend's display name ("default" for local, else the registered agent name). */
  locationName: string;
  isLocal: boolean;
}

const [byBackend, setByBackend] = createSignal<Map<string, SessionChip[]>>(new Map());
const [status, setStatus] = createSignal<SessionStatusName | undefined>(undefined);

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

// The merged rail: every backend's chips, local first. A chip is only shown active when it belongs to the
// backend currently driving the page, so there's never a second highlighted chip from a background backend.
const merged = createMemo<RailSession[]>(() => {
  const active = activeBackendId();
  const out: RailSession[] = [];
  for (const [backendId, chips] of byBackend()) {
    const isLocal = backendId === "local";
    for (const chip of chips) {
      out.push({
        ...chip,
        backendId,
        isLocal,
        locationName: backendName(backendId),
        active: chip.active && backendId === active,
      });
    }
  }
  return out;
});

/** The merged sessions for the left rail, across all backends (local + remotes). */
export const sessions = merged;

/** The active session's Claude status for the pane-head dot, or undefined until the first push. */
export const claudeStatus = status;
