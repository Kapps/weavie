import { createSignal } from "solid-js";
import { onSessionMessage, postToLocalHost } from "../bridge";

// App-global session-rail UI state, persisted host-side in ~/.weavie/rail-state.json (not localStorage): the
// backend a session was last created on, and which remote sessions are promoted into the rail. Setters update
// the local signal optimistically and tell the host, which echoes the canonical state back. Registered at
// module load, before main.tsx sends `ready`.

const [lastLocationSig, setLastLocationSig] = createSignal("local");
const [promotedSig, setPromotedSig] = createSignal<Set<string>>(new Set());

// The session ids last seen on each backend, so a one-shot auto-promote can pick out the GENUINELY new
// session (rather than guessing "whichever is active", which mis-fires when the backend already had an
// active session or sends an unrelated refresh first).
const knownByBackend = new Map<string, Set<string>>();
// Remote backends whose next-created session should be auto-promoted, mapped to the id snapshot taken when
// the creation was kicked off; the first later session-list with a new id promotes it (one-shot).
const pendingPromote = new Map<string, Set<string>>();

// Honored only from the LOCAL backend — a remote runner would push its own rail state, which must not leak in.
onSessionMessage((message, backendId) => {
  if (message.type === "rail-state" && backendId === "local") {
    setLastLocationSig(message.lastLocation);
    setPromotedSig(new Set(message.promoted));
  } else if (message.type === "session-list") {
    const snapshot = pendingPromote.get(backendId);
    if (snapshot !== undefined) {
      // Prefer an active new session, else the first new id; if none is new yet, keep waiting for the list
      // that includes it rather than consuming the one-shot on a stale refresh.
      const fresh = message.sessions.filter((s) => !snapshot.has(s.id));
      const created = fresh.find((s) => s.active) ?? fresh[0];
      if (created !== undefined) {
        pendingPromote.delete(backendId);
        promoteSession(backendId, created.id);
      }
    }
    knownByBackend.set(backendId, new Set(message.sessions.map((s) => s.id)));
  }
});

const promKey = (backendId: string, id: string): string => `${backendId} ${id}`;

/** The backend id the last session was created on (defaults to "local"). The caller validates it still exists. */
export const lastLocation = lastLocationSig;

/** Remember the backend a session was just created on (or an agent just added), for the next prompt. */
export function setLastLocation(backendId: string): void {
  setLastLocationSig(backendId);
  postToLocalHost({ type: "set-last-location", location: backendId });
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

/** Promote the next NEW session a remote backend reports — snapshots its current ids so the reply's id stands out. */
export function promoteNextSessionOn(backendId: string): void {
  if (backendId !== "local") {
    pendingPromote.set(backendId, new Set(knownByBackend.get(backendId) ?? []));
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
  postToLocalHost({ type: "set-promoted", promoted: [...promotedSig()] });
}
