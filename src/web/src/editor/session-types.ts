// Editor-session shape mirrored from Weavie.Core.Editor across the bridge (camelCase JSON). Carries no file
// file contents — disk is the source of truth; source/plan content is restored by its owning session feature.

// Opaque Monaco view state (scroll + cursor + folding) from editor.saveViewState(); stored and restored
// verbatim, never interpreted here.
export type EditorViewState = unknown;

export interface EditorSessionEntry {
  path: string;
  // Tab kind: a file, iframe web tab, fetched source document, or read-only transient plan.
  // Overlay kinds are session-owned and round-trip with the tab set. Absent ⇒ "file".
  kind?: "file" | "web" | "source" | "plan";
  viewState: EditorViewState | null;
  // Preview tab: reused by the next preview open (single-click / go-to-def), shown italic; promoted to a
  // persistent tab by editing or double-clicking. Absent ⇒ false.
  preview?: boolean;
  // Pinned tab: compact, sorted furthest-left, protected from bulk-close. Absent ⇒ false.
  pinned?: boolean;
  // Scratch (untitled) buffer backed by a temp file outside the workspace: shown as "Untitled-N", saving
  // prompts for a real name, closing discards it. Absent ⇒ false.
  scratch?: boolean;
}

export interface EditorSession {
  active: string | null;
  open: EditorSessionEntry[];
}
