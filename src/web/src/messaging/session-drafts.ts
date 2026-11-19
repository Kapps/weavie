import type { ClientSession } from "../bridge";
import { notify } from "../notify/notify";

const PREFIX = "weavie-session-draft";
const failedKeys = new Set<string>();
// Drafts outlive a full storage quota for as long as this page does; storage only serves a refresh.
const memory = new Map<string, string>();
// Departed slots whose stored drafts storage refused to remove: never read back, and removed at the next chance.
const uncleared = new Set<string>();

export function sessionDraft(session: ClientSession, kind: string): string {
  const key = draftKey(session, kind);
  const held = memory.get(key);
  if (
    held !== undefined ||
    uncleared.has(slotSuffix(session.connection.id, session.address.slot))
  ) {
    return held ?? "";
  }
  try {
    return window.sessionStorage.getItem(key) ?? "";
  } catch (error) {
    reportStorageError(key, error);
    return "";
  }
}

export function persistSessionDraft(session: ClientSession, kind: string, value: string): void {
  const key = draftKey(session, kind);
  memory.set(key, value);
  try {
    // A slot back in use first loses what it held when it departed.
    if (uncleared.has(slotSuffix(session.connection.id, session.address.slot))) {
      removeDeparted();
    }
    if (value.length === 0) {
      window.sessionStorage.removeItem(key);
    } else {
      window.sessionStorage.setItem(key, value);
    }
    failedKeys.delete(key);
  } catch (error) {
    reportStorageError(key, error);
  }
}

/** Forgets a departed slot's drafts, so a reused slot can't inherit them. */
export function clearSlotDrafts(connectionId: string, slot: string): void {
  const suffix = slotSuffix(connectionId, slot);
  for (const key of memory.keys()) {
    if (key.endsWith(suffix)) {
      memory.delete(key);
    }
  }
  uncleared.add(suffix);
  try {
    removeDeparted();
  } catch (error) {
    reportStorageError(`${PREFIX}${suffix}`, error);
  }
}

// Removes the stored drafts of every departed slot; while storage refuses, they stay departed.
function removeDeparted(): void {
  for (const key of Object.keys(window.sessionStorage)) {
    if (key.startsWith(PREFIX) && [...uncleared].some((departed) => key.endsWith(departed))) {
      window.sessionStorage.removeItem(key);
    }
  }
  uncleared.clear();
}

const slotSuffix = (connectionId: string, slot: string): string => `:${connectionId}:${slot}`;

function draftKey(session: ClientSession, kind: string): string {
  return `${PREFIX}:${kind}${slotSuffix(session.connection.id, session.address.slot)}`;
}

function reportStorageError(key: string, error: unknown): void {
  if (failedKeys.has(key)) {
    return;
  }
  failedKeys.add(key);
  notify(
    "error",
    `Couldn't preserve pending input across a reload: ${error instanceof Error ? error.message : String(error)}`,
  );
}
