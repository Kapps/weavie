// Editor-session shape mirrored from Weavie.Core.Editor across the bridge (camelCase JSON). Carries no file
// contents — disk is the source of truth; on restore each file is reopened as a working copy from disk.

// Opaque Monaco view state (scroll + cursor + folding) from editor.saveViewState(); stored and restored
// verbatim, never interpreted here.
export type EditorViewState = unknown;

export interface EditorSessionEntry {
  path: string;
  // Tab kind: a file tab (Monaco, the default — absent ⇒ "file"), a "web" tab (an http(s) URL in an iframe,
  // where `path` is the URL), or a "source" tab (a fetched third-party doc — Notion — rendered as rich HTML in a
  // shadow root, where `path` is the source target). web/source are web-only overlay surfaces. Absent ⇒ "file".
  kind?: "file" | "web" | "source";
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
