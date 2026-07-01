import { createSignal } from "solid-js";
import { normalizePath } from "./fs-path";

// Per-file unsaved-changes state surfaced as the tab strip's `*` marker. Local UI only — never persisted or
// sent to the host. Keyed by normalized identity (see fs-path.ts normalizePath) so a URI-derived path always
// matches a host-native tab path, whatever the browser/host OS pair. Top-level module signal so it survives
// Vite hot reload, out of the dynamic editor chunk.
const [dirty, setDirty] = createSignal<ReadonlySet<string>>(new Set());

/// The set of normalized paths with unsaved changes (reactive). For iteration; membership tests go
/// through `isDirtyPath`, which normalizes the query.
export const dirtyPaths = dirty;

/// True when `path` (any spelling) has unsaved changes. Reactive when read inside a tracking scope.
export function isDirtyPath(path: string): boolean {
  return dirty().has(normalizePath(path));
}

/// Marks `path` dirty or clean. Replaces the set only on a real change to avoid no-op re-renders.
export function setDirtyPath(path: string, isDirty: boolean): void {
  const key = normalizePath(path);
  const current = dirty();
  if (isDirty === current.has(key)) {
    return;
  }
  const next = new Set(current);
  if (isDirty) {
    next.add(key);
  } else {
    next.delete(key);
  }
  setDirty(next);
}
