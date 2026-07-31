import { createSignal } from "solid-js";
import { hostConnection, LOCAL_BACKEND_ID, registerHostFeature, selectedSession } from "../bridge";

// App-global session-rail UI state, persisted host-side in ~/.weavie/rail-state.json (not localStorage): the
// backend a session was last created on, and which remote sessions are promoted into the rail. Setters update
// the local signal optimistically and tell the host, which echoes the canonical state back. Registered at
// module load, before main.tsx sends `ready`.

const [lastLocationSig, setLastLocationSig] = createSignal("local");
const [promotedSig, setPromotedSig] = createSignal<Set<string>>(new Set());

// The session ids last seen on each backend, so a one-shot auto-promote can pick out the GENUINELY new
// session (rather than guessing from client selection, which can point at an existing session).
const knownByBackend = new Map<string, Set<string>>();
// Remote backends whose next-created session should be auto-promoted, mapped to the id snapshot taken when
// the creation was kicked off; the first later catalog with a new id promotes it (one-shot).
const pendingPromote = new Map<string, Set<string>>();

interface RailState {
  lastLocation: string;
  promoted: string[];
}

function applyRailState(state: RailState): void {
  setLastLocationSig(state.lastLocation);
  setPromotedSig(new Set(state.promoted));
}

registerHostFeature((connection) => {
  const offCatalog = connection.onCatalog((catalog) => {
    const snapshot = pendingPromote.get(connection.id);
    if (snapshot !== undefined) {
      const fresh = catalog.filter((session) => !snapshot.has(session.id));
      const selected = selectedSession();
      const created =
        fresh.find(
          (session) =>
            selected?.connection === connection && selected.address.slot === session.address?.slot,
        ) ?? fresh[0];
      if (created !== undefined) {
        pendingPromote.delete(connection.id);
        promoteSession(connection.id, created.id);
      }
    }
    knownByBackend.set(connection.id, new Set(catalog.map((session) => session.id)));
  });
  if (!connection.isLocal) {
    return offCatalog;
  }
  const offHello = connection.onHello((hello) => applyRailState(hello.rail));
  const offState = connection.host.feature("rail").on<RailState>("changed", applyRailState);
  return () => {
    offCatalog();
    offHello();
    offState();
  };
});

const promKey = (backendId: string, id: string): string => `${backendId} ${id}`;

/** The backend id the last session was created on (defaults to "local"). The caller validates it still exists. */
export const lastLocation = lastLocationSig;

/** Remember the backend a session was just created on (or an agent just added), for the next prompt. */
export function setLastLocation(backendId: string): void {
  setLastLocationSig(backendId);
  hostConnection(LOCAL_BACKEND_ID)
    ?.host.feature("rail")
    .publish("setLastLocation", { location: backendId });
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
  hostConnection(LOCAL_BACKEND_ID)
    ?.host.feature("rail")
    .publish("setPromoted", { promoted: [...promotedSig()] });
}
