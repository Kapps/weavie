import type { ClientSession } from "../bridge";
import { createSessionOwnedState } from "../messaging/session-owned-state";
import { normalizePath } from "./fs-path";

const EMPTY = new Set<string>();
const bySession = createSessionOwnedState<ReadonlySet<string>>(() => EMPTY);

export function dirtyPathsFor(session: ClientSession | null): ReadonlySet<string> {
  return bySession.get(session) ?? EMPTY;
}

export function isDirtyPath(session: ClientSession | null, path: string): boolean {
  return dirtyPathsFor(session).has(normalizePath(path));
}

export function setDirtyPath(session: ClientSession, path: string, isDirty: boolean): void {
  const key = normalizePath(path);
  const current = bySession.get(session) ?? EMPTY;
  if (isDirty === current.has(key)) {
    return;
  }
  const files = new Set(current);
  if (isDirty) {
    files.add(key);
  } else {
    files.delete(key);
  }
  bySession.update(session, () => files);
}
