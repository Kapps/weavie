// Editor-session shape mirrored from Weavie.Core.Editor as it crosses the bridge (camelCase JSON). The
// persisted/on-the-wire shape carries NO file contents — disk is the source of truth: on restore the web
// reopens each file as a working copy resolved from disk through the host file provider, so no content
// needs to ride along.

// Opaque Monaco view state (scroll + cursor + folding) from editor.saveViewState(); stored and restored
// verbatim, never interpreted here.
export type EditorViewState = unknown;

export interface EditorSessionEntry {
  path: string;
  viewState: EditorViewState | null;
  // A preview tab is reused by the next preview open (single-click / go-to-def) and shown italic; promoted
  // to a persistent tab by editing the file or double-clicking. Absent ⇒ false.
  preview?: boolean;
  // A pinned tab is compact, sorted furthest-left, and protected from bulk-close. Absent ⇒ false.
  pinned?: boolean;
  // A scratch (untitled) buffer backed by a temp file outside the workspace: shown as "Untitled-N", saving
  // prompts for a real name, closing discards it. Round-trips so a restored scratch keeps its identity.
  // Absent ⇒ false.
  scratch?: boolean;
}

export interface EditorSession {
  active: string | null;
  open: EditorSessionEntry[];
}
