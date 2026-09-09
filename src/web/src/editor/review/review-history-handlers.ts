import type { ClientSession } from "../../bridge";
import { notify } from "../../notify/notify";
import type { ReviewHistoryHandlers } from "../inline-diff";
import type { TextLocation } from "../nav-history";

/** History mutates its session; the invoking connection owns the resulting reveal. */
export function reviewHistoryHandlers(
  session: ClientSession,
  captureReveal: () => (location: TextLocation) => void,
): ReviewHistoryHandlers {
  const run = (operation: "undo" | "redo", args: { kind?: "keep" | "revert" }): void => {
    const reveal = captureReveal();
    void session
      .feature("review")
      .request<TextLocation | null, typeof args>(operation, args)
      .then((location) => {
        if (location !== null) reveal(location);
      })
      .catch((error: unknown) =>
        notify("warn", `Couldn't ${operation} review decision: ${String(error)}`),
      );
  };
  return {
    onUndoKeep: () => run("undo", { kind: "keep" }),
    onUndoRevert: () => run("undo", { kind: "revert" }),
    onUndoLast: () => run("undo", {}),
    onRedo: () => run("redo", {}),
  };
}
