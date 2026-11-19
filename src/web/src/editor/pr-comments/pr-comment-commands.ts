import type { ClientSession } from "../../bridge";
import { setContext } from "../../commands/context";
import { registerCommand } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import { onScreenContext } from "../../messaging/on-screen";
import { editorContexts } from "../editor-context";
import { requestFinishReview } from "./FinishReview";
import { actOnFocusedComposer } from "./PrCommentComposer";
import { installPrCommentRefresh } from "./pr-comment-refresh";
import { prCommentsFor } from "./pr-comments-store";

const finish = (kind: "open" | "discard", session: ClientSession | null): boolean => {
  if (session === null) return false;
  void requestFinishReview(session, kind);
  return true;
};

/** The comment-box and review commands plus comment refresh; per-line commenting is an editor command. */
export function registerPrCommentCommands(): () => void {
  const cleanups = [
    installPrCommentRefresh(),
    registerCommand(CommandIds.prSubmitComment, () => actOnFocusedComposer("submit")),
    registerCommand(CommandIds.prPostCommentNow, () => actOnFocusedComposer("alternate")),
    registerCommand(CommandIds.prCancelComment, () => actOnFocusedComposer("cancel")),
    registerCommand(CommandIds.prFinishReview, (_args, { session }) => finish("open", session)),
    registerCommand(CommandIds.prDiscardReview, (_args, { session }) => finish("discard", session)),
    onScreenContext("prReviewable", (session) => prCommentsFor(session)?.set != null),
    onScreenContext("prReviewPending", (session) => prCommentsFor(session)?.set?.pending != null),
    editorContexts.onChange((connection) =>
      setContext("prCommentable", connection.prComments.commentable()),
    ),
  ];
  return () => {
    for (const cleanup of cleanups) cleanup();
  };
}
