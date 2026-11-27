import { sendAgentSelection } from "../agent/composer-store";
import { createSelectionActions, sendToConversation } from "../agent/selection-actions";
import { sessions } from "../chrome/session-store";
import { currentEditorOptions } from "../editor-options";
import { editorContexts, type TextEditorConnection } from "./editor-context";
import {
  addInlineReply,
  anchorPlace,
  type FileAnchor,
} from "./inline-replies/inline-replies-store";
import { monaco } from "./monaco-setup";
import { anchorPath, selectionExcerpt, selectionQuote } from "./selection-excerpt";
import { watchSelectionGestures } from "./selection-gestures";

/** Positions actions only on completed gestures; typing and scroll frames never measure layout. */
export function connectSelectionActions(connection: TextEditorConnection): () => void {
  const { editor, model, session } = connection;
  const actions = createSelectionActions(editor.getDomNode()!, session, () => null);
  const unwatch = watchSelectionGestures(editor, actions, (selection, point) => {
    if (
      editorContexts.get(session) !== connection ||
      sessions().find((entry) => entry.owner === session)?.agentSurface !== "structured"
    )
      return;
    actions.show(selectionExcerpt(model, selection), point, (owner, excerpt, comment) => {
      const quoted = currentEditorOptions().inlineReplies ? selectionQuote(model, selection) : null;
      if (quoted === null) {
        sendToConversation(owner, excerpt, comment);
        return;
      }
      const sent = sendAgentSelection(owner, excerpt, comment, {
        path: anchorPath(model),
        conversationId: null,
        blockId: null,
        place: anchorPlace(quoted.range),
        quote: quoted.quote,
      });
      if (sent?.anchor == null) return;
      addInlineReply(owner, model, quoted.range, sent.anchor as FileAnchor, comment);
      // The reply opens below the selection, so one low in the view moves up to leave it room.
      const lastLine = quoted.range.endLineNumber;
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
