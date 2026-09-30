import { setContext } from "../../commands/context";
import { registerCommand } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import { editorContexts } from "../editor-context";
import { actOnFocusedComposer } from "./PrCommentComposer";
import { togglePrComments } from "./pr-comments-store";

/** The comment-box and visibility commands; per-line commenting is bound with the editor commands. */
export function registerPrCommentCommands(): () => void {
  const cleanups = [
    registerCommand(CommandIds.prSubmitComment, () => actOnFocusedComposer("submit")),
    registerCommand(CommandIds.prCancelComment, () => actOnFocusedComposer("cancel")),
    registerCommand(CommandIds.prToggleComments, () => {
      togglePrComments();
      return true;
    }),
    editorContexts.onChange((connection) =>
      setContext("prCommentable", connection.prComments.commentable()),
    ),
  ];
  return () => {
    for (const cleanup of cleanups) cleanup();
  };
}
