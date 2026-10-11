import { createEffect, createMemo, createRoot, createSignal, onCleanup } from "solid-js";
import { selectedSession } from "../../bridge";
import { prOverviewFor } from "../review/overview/overview-data";
import { prCommentsFor } from "./pr-comments-store";

// Each poll is one point of the viewer's GitHub GraphQL budget (5,000/hour), so only the visible session polls.
const REFRESH_MS = 60_000;

/**
 * Keeps PR comments and the PR overview fresh while the user can see them — the selected session, in a visible
 * window — on selection, on returning to the window, and every minute. They're forge state, unrelated to what the
 * agent is doing. Importing the overview's store here also subscribes it from startup, before the review tab loads.
 */
export function installPrCommentRefresh(): () => void {
  return createRoot((dispose) => {
    const [visible, setVisible] = createSignal(document.visibilityState === "visible");
    const onVisibility = (): void => {
      setVisible(document.visibilityState === "visible");
    };
    const watched = createMemo(() => {
      const session = selectedSession();
      const pr =
        session !== null &&
        (prCommentsFor(session)?.set != null || prOverviewFor(session)?.detail != null);
      return visible() && pr ? session : null;
    });
    const refresh = (): void => watched()?.feature("pullRequests").publish("refresh", {});
    createEffect(() => {
      if (watched() === null) return;
      refresh();
      const timer = setInterval(refresh, REFRESH_MS);
      onCleanup(() => clearInterval(timer));
    });
    document.addEventListener("visibilitychange", onVisibility);
    window.addEventListener("focus", refresh);
    return () => {
      document.removeEventListener("visibilitychange", onVisibility);
      window.removeEventListener("focus", refresh);
      dispose();
    };
  });
}
