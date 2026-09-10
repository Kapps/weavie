import { createSignal } from "solid-js";
import { selectedSession } from "../bridge";
import { editorContexts, type TextEditorConnection } from "./editor-context";

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

const [snapshot, setSnapshot] = createSignal<{
  connection: TextEditorConnection;
  status: EditorStatus;
} | null>(null);

/** The visible text connection's cursor/selection/EOL, or null without a matching snapshot. */
export function editorStatus(): EditorStatus | null {
  const current = snapshot();
  const session = selectedSession();
  return current !== null &&
    session === current.connection.session &&
    editorContexts.get(session) === current.connection
    ? current.status
    : null;
}

/** Publishes status owned by the exact text connection. */
export function setEditorStatus(connection: TextEditorConnection, status: EditorStatus): void {
  setSnapshot({ connection, status });
}

/** Retiring an old connection cannot clear a newer connection's status. */
export function clearEditorStatus(connection: TextEditorConnection): void {
  setSnapshot((current) => (current?.connection === connection ? null : current));
}
