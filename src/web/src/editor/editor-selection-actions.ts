import { createSelectionActions } from "../agent/selection-actions";
import { sessions } from "../chrome/session-store";
import { currentEditorOptions } from "../editor-options";
import { editorContexts, type TextEditorConnection } from "./editor-context";
import { addInlineReply } from "./inline-replies/inline-replies-store";
import { monaco } from "./monaco-setup";
import { SESSION_FILE_SCHEME, sessionUriHostPath } from "./session-uri";

/** Positions actions only on completed gestures; typing and scroll frames never measure layout. */
export function connectSelectionActions(connection: TextEditorConnection): () => void {
  const { editor, model, session } = connection;
  const host = editor.getDomNode()!;
  const actions = createSelectionActions(host, session);
  let changed = false;
  // Where the shown selection's line sits in the view, to tell it moving from content elsewhere resizing.
  let shownTop: number | null = null;
  const top = (line: number): number => editor.getTopForLineNumber(line) - editor.getScrollTop();
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
    const text = model.getValueInRange(selection);
    const rect = host.getBoundingClientRect();
    // A selection ending at a line's start covers the line above it.
    shownTop = top(selection.getPosition().lineNumber);
    const lastLine =
      selection.endColumn === 1 && selection.endLineNumber > selection.startLineNumber
        ? selection.endLineNumber - 1
        : selection.endLineNumber;
    actions.show(
      { label, text: `${source}\n${text}` },
      { x: rect.left + position.left, y: rect.top + position.top + position.height },
      (id, comment) => {
        if (!currentEditorOptions().inlineReplies) return;
        addInlineReply(session, model, lastLine, id, comment);
        // The reply opens below the line, so a line low in the view moves up to leave it room.
        const { height } = editor.getLayoutInfo();
        if (editor.getTopForLineNumber(lastLine) - editor.getScrollTop() > height / 2)
          editor.revealLineInCenter(lastLine, monaco.editor.ScrollType.Smooth);
      },
    );
  };
  const selection = editor.onDidChangeCursorSelection(() => {
    changed = true;
    shownTop = null;
    actions.clear();
  });
  const scroll = editor.onDidScrollChange((event) => {
    const line = editor.getSelection()?.getPosition().lineNumber;
    if (shownTop === null || line === undefined) return;
    if (event.scrollLeftChanged || top(line) !== shownTop) actions.moved();
  });
  const layout = editor.onDidLayoutChange(actions.moved);
  const mouse = editor.onMouseUp(show);
  const key = editor.onKeyUp(show);
  const content = model.onDidChangeContent(actions.clear);
  return () => {
    selection.dispose();
    scroll.dispose();
    layout.dispose();
    mouse.dispose();
    key.dispose();
    content.dispose();
    actions.dispose();
  };
}
