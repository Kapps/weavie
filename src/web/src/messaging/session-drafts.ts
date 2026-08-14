import type { ClientSession } from "../bridge";
import { notify } from "../notify/notify";

const PREFIX = "weavie-session-draft";
const failedKeys = new Set<string>();

export function sessionDraft(session: ClientSession, kind: string): string {
  const key = draftKey(session, kind);
  try {
    return window.sessionStorage.getItem(key) ?? "";
  } catch (error) {
    reportStorageError(key, error);
    return "";
  }
}

export function persistSessionDraft(session: ClientSession, kind: string, value: string): void {
  const key = draftKey(session, kind);
  try {
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

function draftKey(session: ClientSession, kind: string): string {
  return `${PREFIX}:${kind}:${session.connection.id}:${session.address.slot}`;
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
