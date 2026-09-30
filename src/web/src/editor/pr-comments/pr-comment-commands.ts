import { setContext } from "../../commands/context";
import { registerCommand } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import { editorContexts } from "../editor-context";
import { actOnFocusedComposer } from "./PrCommentComposer";
import { installPrCommentRefresh } from "./pr-comment-refresh";
import { togglePrComments } from "./pr-comments-store";

/** The comment-box and visibility commands plus comment refresh; per-line commenting is an editor command. */
export function registerPrCommentCommands(): () => void {
  const cleanups = [
    installPrCommentRefresh(),
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
