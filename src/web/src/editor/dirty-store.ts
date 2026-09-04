import { type ClientSession, selectedSession } from "../bridge";
import { createSessionOwnedState } from "../messaging/session-owned-state";
import { normalizePath } from "./fs-path";

const EMPTY = new Set<string>();
const bySession = createSessionOwnedState<ReadonlySet<string>>(() => EMPTY);

export const dirtyPaths = (): ReadonlySet<string> => bySession.get(selectedSession()) ?? EMPTY;

export function dirtyPathsFor(session: ClientSession): ReadonlySet<string> {
  return bySession.get(session) ?? EMPTY;
}

export function isDirtyPath(path: string): boolean {
  return dirtyPaths().has(normalizePath(path));
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
