import { createSignal } from "solid-js";
import { onSessionMessage, postToBackend } from "../bridge";

// App-global session-rail UI state, persisted host-side in ~/.weavie/rail-state.json (not localStorage): the
// backend a session was last created on, and which remote sessions are promoted into the rail. Setters update
// the local signal optimistically and tell the host, which echoes the canonical state back. Registered at
// module load, before main.tsx sends `ready`.

const [lastLocationSig, setLastLocationSig] = createSignal("local");
const [promotedSig, setPromotedSig] = createSignal<Set<string>>(new Set());

// Remote backends whose next-created session should be auto-promoted: the id isn't known until the backend
// pushes its session-list, so we promote the active session there (one-shot per creation).
const pendingPromote = new Set<string>();

// Honored only from the LOCAL backend — a remote runner would push its own rail state, which must not leak in.
onSessionMessage((message, backendId) => {
  if (message.type === "rail-state" && backendId === "local") {
    setLastLocationSig(message.lastLocation);
    setPromotedSig(new Set(message.promoted));
  } else if (message.type === "session-list" && pendingPromote.has(backendId)) {
    const created = message.sessions.find((s) => s.active);
    if (created !== undefined) {
      pendingPromote.delete(backendId);
      promoteSession(backendId, created.id);
    }
  }
});

const promKey = (backendId: string, id: string): string => `${backendId} ${id}`;

/** The backend id the last session was created on (defaults to "local"). The caller validates it still exists. */
export const lastLocation = lastLocationSig;

/** Remember the backend a session was just created on (or an agent just added), for the next prompt. */
export function setLastLocation(backendId: string): void {
  setLastLocationSig(backendId);
  postToBackend("local", { type: "set-last-location", location: backendId });
}

/** The promoted-session keys (reactive), for the rail's working-set filter. */
export const promotedKeys = promotedSig;

/** Whether a remote session is currently promoted into the rail. */
export function isPromoted(backendId: string, id: string): boolean {
  return promotedSig().has(promKey(backendId, id));
}

/** Pull a remote session into the rail (the working set). Idempotent. */
export function promoteSession(backendId: string, id: string): void {
  const key = promKey(backendId, id);
  if (promotedSig().has(key)) {
    return;
  }
  setPromotedSig((prev) => new Set(prev).add(key));
  pushPromoted();
}

/** Promote whatever session a remote backend makes active next — its new-session reply lands the id. */
export function promoteNextSessionOn(backendId: string): void {
  if (backendId !== "local") {
    pendingPromote.add(backendId);
  }
}

/** Drop a promoted remote session from the rail (it stays available in the cloud panel). */
export function demoteSession(backendId: string, id: string): void {
  const key = promKey(backendId, id);
  if (!promotedSig().has(key)) {
    return;
  }
  setPromotedSig((prev) => {
    const next = new Set(prev);
    next.delete(key);
    return next;
  });
  pushPromoted();
}

function pushPromoted(): void {
  postToBackend("local", { type: "set-promoted", promoted: [...promotedSig()] });
}
