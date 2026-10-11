import { createEffect, createRoot } from "solid-js";
import { selectedSession } from "../../bridge";
import { setContext } from "../../commands/context";
import { registerCommand } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import { editorContexts } from "../editor-context";
import { requestFinishReview } from "./FinishReview";
import { actOnFocusedComposer } from "./PrCommentComposer";
import { installPrCommentRefresh } from "./pr-comment-refresh";
import { prCommentsFor, togglePrComments } from "./pr-comments-store";

// Whether the selected session has a PR to review, and a pending review on it.
function trackReviewContext(): () => void {
  return createRoot((dispose) => {
    createEffect(() => {
      const set = prCommentsFor(selectedSession())?.set ?? null;
      setContext("prReviewable", set !== null);
      setContext("prReviewPending", set?.pending != null);
    });
    return dispose;
  });
}

const finish = (kind: "open" | "discard"): boolean => {
  const session = selectedSession();
  if (session === null) return false;
  void requestFinishReview(session, kind);
  return true;
};

/** The comment-box, review, and visibility commands plus comment refresh; per-line commenting is an editor command. */
export function registerPrCommentCommands(): () => void {
  const cleanups = [
    installPrCommentRefresh(),
    registerCommand(CommandIds.prSubmitComment, () => actOnFocusedComposer("submit")),
    registerCommand(CommandIds.prPostCommentNow, () => actOnFocusedComposer("alternate")),
    registerCommand(CommandIds.prCancelComment, () => actOnFocusedComposer("cancel")),
    registerCommand(CommandIds.prFinishReview, () => finish("open")),
    registerCommand(CommandIds.prDiscardReview, () => finish("discard")),
    trackReviewContext(),
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
