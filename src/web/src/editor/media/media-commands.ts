import type { ClientSession } from "../../bridge";

/** A mounted media pane's view actions. */
export interface MediaPaneActions {
  rotate(): void;
  toggleFullscreen(): void;
}

// Mounted media panes by owning session, so the media commands act on the invoking session's visible pane.
const panes = new Map<ClientSession, MediaPaneActions>();

/** Registers `actions` as `session`'s media pane; returns the unregister. */
export function registerMediaPane(session: ClientSession, actions: MediaPaneActions): () => void {
  panes.set(session, actions);
  return () => {
    if (panes.get(session) === actions) panes.delete(session);
  };
}

/** Runs `action` on `session`'s open media; declines when it has no media tab showing. */
export function runMediaAction(
  session: ClientSession | null,
  action: keyof MediaPaneActions,
): boolean {
  const pane = session === null ? undefined : panes.get(session);
  pane?.[action]();
  return pane !== undefined;
}
