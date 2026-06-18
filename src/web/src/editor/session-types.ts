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
}

export interface EditorSession {
  active: string | null;
  open: EditorSessionEntry[];
}
