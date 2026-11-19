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
  if (held !== undefined || uncleared.has(slotOf(session))) {
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
    if (uncleared.has(slotOf(session))) {
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
  const departed = `${connectionId}:${slot}`;
  for (const key of memory.keys()) {
    if (slotOfKey(key) === departed) {
      memory.delete(key);
    }
  }
  uncleared.add(departed);
  try {
    removeDeparted();
  } catch (error) {
    reportStorageError(`${PREFIX}:${departed}`, error);
  }
}

// Removes the stored drafts of every departed slot; while storage refuses, they stay departed.
function removeDeparted(): void {
  for (const key of Object.keys(window.sessionStorage)) {
    if (key.startsWith(`${PREFIX}:`) && uncleared.has(slotOfKey(key))) {
      window.sessionStorage.removeItem(key);
    }
  }
  uncleared.clear();
}

// A draft is keyed `<prefix>:<kind>:<connection id>:<slot>`; a kind has no colon, a connection id can.
const slotOf = (session: ClientSession): string =>
  `${session.connection.id}:${session.address.slot}`;

const slotOfKey = (key: string): string => key.slice(key.indexOf(":", PREFIX.length + 1) + 1);

function draftKey(session: ClientSession, kind: string): string {
  return `${PREFIX}:${kind}:${slotOf(session)}`;
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
