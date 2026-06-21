import { createMemo, createSignal } from "solid-js";
import {
  type SessionChip,
  type SessionStatusName,
  activeBackendId,
  backendName,
  onSessionMessage,
} from "../bridge";

// The session rail aggregates sessions from every connected backend (the local host plus each registered
// remote agent) into one rail. Each backend pushes its own session-list, kept keyed by backend and rendered
// as a union, tagging each chip with its location. Top-level module signals so they survive HMR; a component
// signal would blank the rail on every hot reload.

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

// The merged rail: every backend's chips, local first. A chip is shown active only when it belongs to the
// backend currently driving the page, so a background backend never shows a second highlighted chip.
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
