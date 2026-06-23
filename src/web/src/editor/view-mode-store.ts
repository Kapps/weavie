import { createSignal } from "solid-js";
import { canonicalFsPath } from "./fs-path";

// Per-file view mode (Source vs Preview) for open files, surfaced as the editor's rendered-Preview overlay
// and the tab strip's toggle. Local UI only — never persisted or sent to the host; any path not in the set
// is Source (the default). A top-level module signal so it survives Vite hot reload and isn't bundled into
// the dynamic editor chunk. Keyed by canonical fs-path (see fs-path.ts) to match the host's path spelling.
const [preview, setPreview] = createSignal<ReadonlySet<string>>(new Set());

/// Whether `path` is in Preview mode (reactive when read in a tracking scope).
export function isPreviewMode(path: string): boolean {
  return preview().has(canonicalFsPath(path));
}

/// Flips `path` between Source and Preview and returns the new mode. Callers gate on preview capability.
export function toggleViewMode(path: string): "source" | "preview" {
  const key = canonicalFsPath(path);
  const current = preview();
  const next = new Set(current);
  const nowPreview = !current.has(key);
  if (nowPreview) {
    next.add(key);
  } else {
    next.delete(key);
  }
  setPreview(next);
  return nowPreview ? "preview" : "source";
}
