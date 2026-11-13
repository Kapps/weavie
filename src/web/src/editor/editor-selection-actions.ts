import { createSelectionActions } from "../agent/selection-actions";
import { sessions } from "../chrome/session-store";
import { editorContexts, type TextEditorConnection } from "./editor-context";
import { SESSION_FILE_SCHEME, sessionUriHostPath } from "./session-uri";

/** Positions actions only on completed gestures; typing and scroll frames never measure layout. */
export function connectSelectionActions(connection: TextEditorConnection): () => void {
  const { editor, model, session } = connection;
  const host = editor.getDomNode()!;
  const actions = createSelectionActions(host, session);
  let changed = false;
  const show = (): void => {
    if (!changed) return;
    changed = false;
    if (
      editorContexts.get(session) !== connection ||
      sessions().find((entry) => entry.owner === session)?.agentSurface !== "structured"
    )
      return;
    const selection = editor.getSelection();
    if (selection === null || selection.isEmpty()) {
      actions.clear();
      return;
    }
    const position = editor.getScrolledVisiblePosition(selection.getPosition());
    if (position === null) return;
    const path =
      model.uri.scheme === SESSION_FILE_SCHEME ? sessionUriHostPath(model.uri) : model.uri.path;
    const range = `${selection.startLineNumber}:${selection.startColumn}-${selection.endLineNumber}:${selection.endColumn}`;
    const source = `${path}:${range}`;
    const label = `${path.split(/[\\/]/).at(-1)}:${range}`;
    const rect = host.getBoundingClientRect();
    actions.show(
      { label, text: `${source}\n${model.getValueInRange(selection)}` },
      { x: rect.left + position.left, y: rect.top + position.top + position.height },
    );
  };
  const selection = editor.onDidChangeCursorSelection(() => {
    changed = true;
    actions.clear();
  });
  const scroll = editor.onDidScrollChange(actions.clear);
  const mouse = editor.onMouseUp(show);
  const key = editor.onKeyUp(show);
  const content = model.onDidChangeContent(actions.clear);
  return () => {
    selection.dispose();
    scroll.dispose();
    mouse.dispose();
    key.dispose();
    content.dispose();
    actions.dispose();
  };
}
