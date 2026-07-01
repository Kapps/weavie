import { createSignal } from "solid-js";

/** The active editor's cursor/selection/language/line-ending snapshot, rendered by the editor pane footer. */
export interface EditorStatus {
  /** 1-based cursor line. */
  line: number;
  /** 1-based cursor column. */
  column: number;
  /** Selected character count across all selections; 0 when nothing is selected. */
  selectionCount: number;
  eol: "LF" | "CRLF";
}

// Top-level module signal (HMR-safe, out of the dynamic editor chunk), mirroring dirty-store. Null while the
// editor shows no file model (empty pane, or a web/source overlay tab) so the footer hides its segments.
const [status, setStatus] = createSignal<EditorStatus | null>(null);

/** The active editor's cursor/selection/language/EOL (reactive), or null when no file is in view. */
export const editorStatus = status;

/** Replaces the editor status snapshot; pass null when the editor shows no file model. */
export function setEditorStatus(next: EditorStatus | null): void {
  setStatus(next);
}
