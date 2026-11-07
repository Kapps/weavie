import type { ClientSession } from "../bridge";
import { persistSessionDraft, sessionDraft } from "../messaging/session-drafts";

const KIND = "editor-edits";

/** Unsaved text by path, carried across a session's incarnations in its slot-keyed draft. */
export function carriedEdits(session: ClientSession): Record<string, string> {
  const stored = sessionDraft(session, KIND);
  return stored === "" ? {} : (JSON.parse(stored) as Record<string, string>);
}

export function storeCarriedEdits(session: ClientSession, edits: Record<string, string>): void {
  persistSessionDraft(session, KIND, Object.keys(edits).length === 0 ? "" : JSON.stringify(edits));
}

/** True when the catalog says `session`'s state can't be trusted, so its pending saves can't be relied on. */
export function sessionNeedsReload(session: ClientSession): boolean {
  return session.connection.currentCatalog.some(
    (entry) => entry.id === session.address.slot && entry.reloadRequired !== null,
  );
}

/** True when the catalog now points `session`'s slot at a newer incarnation. */
export function sessionReplaced(session: ClientSession): boolean {
  return session.connection.currentCatalog.some(
    (entry) =>
      entry.id === session.address.slot &&
      entry.address !== null &&
      entry.address.incarnation !== session.address.incarnation,
  );
}
