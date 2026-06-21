import { createSignal } from "solid-js";

// Per-file "unsaved changes" (dirty) state for open working copies, surfaced as the tab strip's `*` marker.
// Local UI only — never persisted or sent to the host; reflects the window between an edit and its debounced
// flush. A top-level module signal so it survives Vite hot reload and isn't bundled into the dynamic editor
// chunk. Keyed by canonical fs-path (see fs-path.ts) so tab lookups match what the host records regardless of
// Windows drive-letter casing.
const [dirty, setDirty] = createSignal<ReadonlySet<string>>(new Set());

/// The set of canonical fs-paths whose working copy has unsaved changes (reactive).
export const dirtyPaths = dirty;

/// Marks `path` dirty or clean. Replaces the set only on a real change to avoid no-op re-renders.
export function setDirtyPath(path: string, isDirty: boolean): void {
  const current = dirty();
  if (isDirty === current.has(path)) {
    return;
  }
  const next = new Set(current);
  if (isDirty) {
    next.add(path);
  } else {
    next.delete(path);
  }
  setDirty(next);
}
