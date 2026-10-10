import type { ClientSession } from "../../bridge";

// Mounted media panes by owning session, so Rotate Media acts on the invoking session's visible pane.
const panes = new Map<ClientSession, () => void>();

/** Registers `rotate` as `session`'s media pane; returns the unregister. */
export function registerMediaPane(session: ClientSession, rotate: () => void): () => void {
  panes.set(session, rotate);
  return () => {
    if (panes.get(session) === rotate) panes.delete(session);
  };
}

/** Rotates `session`'s open media a quarter turn; declines when it has no media tab showing. */
export function rotateMedia(session: ClientSession | null): boolean {
  const rotate = session === null ? undefined : panes.get(session);
  rotate?.();
  return rotate !== undefined;
}
