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
import { type ItemKey, stepItem } from "./review-sequence";

type RevealFile = (session: ClientSession, path: string, line: number) => void;

// From the given item, else the current one, else the cursor; the unified review reveals a file it holds, any other
// view opens a tab. An editor already showing the item claims it at once, at where it sits now.
function step(
  session: ClientSession,
  direction: 1 | -1,
  from: ItemKey | null,
  revealFile: RevealFile,
): void {
  const cursor = activeTabFor(session)?.presentation?.capture().text ?? null;
  const origin =
    from ??
    reviewItems.current(session) ??
    (cursor === null ? null : reviewItems.resumeFrom(session, cursor));
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
  const claimed = reviewItems.claimedLine(session);
  const line = claimed ?? Math.max(1, target.line);
  reviewItems.land(session, target.key, target.path, line);
  // The unified review still reveals a claimed item, which opens a fold over it; a claiming file tab already has.
  if (activeTabFor(session)?.presentation?.actions()?.revealLine?.(target.path, line)) return;
  if (claimed === null) revealFile(session, target.path, line);
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

// A control on one item (a hover popover, a card) steps from that item: `{ from: "hint:3" }`.
function stepOrigin(args: unknown): ItemKey | null {
  const from = (args as { from?: unknown } | undefined)?.from;
  return typeof from === "string" && /^(hint|thread):./.test(from) ? (from as ItemKey) : null;
}

/** Next and Previous Comment or Hint. */
export function registerReviewItemCommands(revealFile: RevealFile): () => void {
  const cleanups = [
    trackAvailability(),
    ...([1, -1] as const).map((direction) =>
      registerCommand(
        direction === 1 ? CommandIds.reviewNextItem : CommandIds.reviewPreviousItem,
        (args, { session }) => {
          if (session !== null) step(session, direction, stepOrigin(args), revealFile);
        },
      ),
    ),
  ];
  return () => {
    for (const cleanup of cleanups) cleanup();
  };
}
