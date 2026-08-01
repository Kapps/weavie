import { createMemo, createSignal } from "solid-js";
import { type ClientSession, registerSessionFeature, selectedSession } from "../bridge";
import { normalizePath } from "./fs-path";

const [bySession, setBySession] = createSignal<Map<ClientSession, ReadonlySet<string>>>(new Map());

export const dirtyPaths = createMemo<ReadonlySet<string>>(() => {
  const session = selectedSession();
  return session === null ? new Set() : (bySession().get(session) ?? new Set());
});

export function dirtyPathsFor(session: ClientSession): ReadonlySet<string> {
  return bySession().get(session) ?? new Set();
}

export function isDirtyPath(path: string): boolean {
  return dirtyPaths().has(normalizePath(path));
}

export function setDirtyPath(session: ClientSession, path: string, isDirty: boolean): void {
  const key = normalizePath(path);
  const current = bySession().get(session) ?? new Set<string>();
  if (isDirty === current.has(key)) {
    return;
  }
  const files = new Set(current);
  if (isDirty) {
    files.add(key);
  } else {
    files.delete(key);
  }
  setBySession((previous) => {
    const next = new Map(previous);
    if (files.size === 0) {
      next.delete(session);
    } else {
      next.set(session, files);
    }
    return next;
  });
}

registerSessionFeature((session) => () => {
  setBySession((previous) => {
    const next = new Map(previous);
    next.delete(session);
    return next;
  });
});
