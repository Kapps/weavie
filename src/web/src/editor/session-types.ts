// Editor-session shape mirrored from Weavie.Core.Editor as it crosses the bridge (camelCase JSON). The
// persisted/on-the-wire shape carries NO file contents — disk is the source of truth — except the host's
// launch restore push (set-editor-session), where each open entry additionally carries `content` so the
// web can seed Monaco models that don't exist yet on a fresh page.

// Opaque Monaco view state (scroll + cursor + folding) from editor.saveViewState(); stored and restored
// verbatim, never interpreted here.
export type EditorViewState = unknown;

export interface EditorSessionEntry {
  path: string;
  viewState: EditorViewState | null;
  // Present only on the host→web restore push; absent on the web→host editor-session-changed.
  content?: string;
}

export interface EditorSession {
  active: string | null;
  open: EditorSessionEntry[];
}
