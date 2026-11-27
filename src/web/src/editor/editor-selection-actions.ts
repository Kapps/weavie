import { createSelectionActions, sendToConversation } from "../agent/selection-actions";
import { sessions } from "../chrome/session-store";
import { currentEditorOptions } from "../editor-options";
import { editorContexts, type TextEditorConnection } from "./editor-context";
import { addInlineReply } from "./inline-replies/inline-replies-store";
import { monaco } from "./monaco-setup";
import { lastLineOf, selectionExcerpt } from "./selection-excerpt";
import { watchSelectionGestures } from "./selection-gestures";

/** Positions actions only on completed gestures; typing and scroll frames never measure layout. */
export function connectSelectionActions(connection: TextEditorConnection): () => void {
  const { editor, model, session } = connection;
  const actions = createSelectionActions(
    editor.getDomNode()!,
    session,
    sendToConversation,
    () => null,
  );
  const unwatch = watchSelectionGestures(editor, actions, (selection, point) => {
    if (
      editorContexts.get(session) !== connection ||
      sessions().find((entry) => entry.owner === session)?.agentSurface !== "structured"
    )
      return;
    actions.show(selectionExcerpt(model, selection), point, (id, comment) => {
      if (!currentEditorOptions().inlineReplies) return;
      addInlineReply(session, model, selection, id, comment);
      // The reply opens below the selection, so one low in the view moves up to leave it room.
      const lastLine = lastLineOf(selection);
      const { height } = editor.getLayoutInfo();
      if (editor.getTopForLineNumber(lastLine) - editor.getScrollTop() > height / 2)
        editor.revealLineInCenter(lastLine, monaco.editor.ScrollType.Smooth);
    });
  });
  return () => {
    unwatch();
    actions.dispose();
  };
}
