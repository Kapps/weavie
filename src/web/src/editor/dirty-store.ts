import { createSignal } from "solid-js";

// Per-file "unsaved changes" (dirty) state for the open working copies, surfaced as the tab strip's `*`
// marker. Purely a local UI concern: it is NEVER persisted or sent to the host — disk is the source of truth
// and autosave keeps it current, so this only reflects the brief window between an edit and its debounced
// (and possibly error-gated) flush. A TOP-LEVEL module signal so it survives a Vite hot reload like the
// session store (the rebuilt editor host re-subscribes to the text-file service and re-seeds it); it must be
// imported at top level (App.tsx → TabStrip) so it isn't reloaded with the dynamic editor chunk.
//
// Keyed by canonical fs-path (see fs-path.ts) so a lookup from a tab entry (`canonicalFsPath(tab.path)`)
// matches what the host records (`model.uri.fsPath`), regardless of Windows drive-letter casing.
const [dirty, setDirty] = createSignal<ReadonlySet<string>>(new Set());

/// The set of canonical fs-paths whose working copy currently has unsaved changes (reactive).
export const dirtyPaths = dirty;

/// Marks `path` dirty or clean. Replaces the set only on a real change, so readers don't re-run for no-ops.
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
