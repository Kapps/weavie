import { createEffect, createRoot } from "solid-js";
import { type ClientSession, selectedSession } from "../../bridge";
import { setContext } from "../../commands/context";
import { registerCommand } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import { notify } from "../../notify/notify";
import { prCommentsFor } from "../pr-comments/pr-comments-store";
import { reviewHintsFor } from "../review-hints/review-hints-store";
import { activeTabFor } from "../session-store";
import { setViewMode } from "../view-mode-store";
import { reviewItems } from "./review-items";
import { stepItem } from "./review-sequence";

type RevealFile = (session: ClientSession, path: string, line: number) => void;

// From the current item, else the cursor; the unified review reveals a file it holds, any other view opens a tab.
function step(session: ClientSession, direction: 1 | -1, revealFile: RevealFile): void {
  const origin =
    reviewItems.current(session) ?? activeTabFor(session)?.presentation?.capture().text ?? null;
  const target = stepItem(
    reviewItems.sequence(session),
    reviewItems.reviewPaths(session),
    origin,
    direction,
  );
  if (target === undefined) {
    notify("info", "There are no comments or hints to step through.", "review-items");
    return;
  }
  setViewMode(session, target.path, "source");
  reviewItems.setCurrent(session, target.key);
  const line = Math.max(1, target.line);
  if (activeTabFor(session)?.presentation?.actions()?.revealLine?.(target.path, line)) return;
  revealFile(session, target.path, line);
}

// Whether the selected session has any thread or hint, before the filter: the stepper's commands need one.
function trackAvailability(): () => void {
  return createRoot((dispose) => {
    createEffect(() => {
      const session = selectedSession();
      const threads = prCommentsFor(session)?.set?.threads.length ?? 0;
      setContext("reviewItemsAvailable", threads + (reviewHintsFor(session)?.length ?? 0) > 0);
    });
    return dispose;
  });
}

/** Next and Previous Comment or Hint. */
export function registerReviewItemCommands(revealFile: RevealFile): () => void {
  const cleanups = [
    trackAvailability(),
    ...([1, -1] as const).map((direction) =>
      registerCommand(
        direction === 1 ? CommandIds.reviewNextItem : CommandIds.reviewPreviousItem,
        (_args, { session }) => {
          if (session !== null) step(session, direction, revealFile);
        },
      ),
    ),
  ];
  return () => {
    for (const cleanup of cleanups) cleanup();
  };
}
