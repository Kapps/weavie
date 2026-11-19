import { createEffect, createMemo, createSignal, onCleanup } from "solid-js";
import { whileOnScreen } from "../../messaging/on-screen";
import { prOverviewFor } from "../review/overview/overview-data";
import { prCommentsFor } from "./pr-comments-store";

// Each poll costs the viewer's GitHub GraphQL budget (5,000 points/hour) a query for the comments and one for the
// overview, plus any paging, so only the visible session polls.
const REFRESH_MS = 60_000;

/**
 * Keeps PR comments and the PR overview fresh while the user can see them — the selected session, in a visible
 * window — on selection, on returning to the window, and every minute. They're forge state, unrelated to what the
 * agent is doing. Importing the overview's store here also subscribes it from startup, before the review tab loads.
 */
export function installPrCommentRefresh(): () => void {
  const [visible, setVisible] = createSignal(document.visibilityState === "visible");
  const onVisibility = (): void => {
    setVisible(document.visibilityState === "visible");
  };
  document.addEventListener("visibilitychange", onVisibility);
  const offView = whileOnScreen((session) => {
    const watched = createMemo(
      () =>
        visible() &&
        (prCommentsFor(session)?.set != null || prOverviewFor(session)?.detail != null),
    );
    const refresh = (): void => {
      if (watched()) session.feature("pullRequests").publish("refresh", {});
    };
    createEffect(() => {
      if (!watched()) return;
      refresh();
      const timer = setInterval(refresh, REFRESH_MS);
      onCleanup(() => clearInterval(timer));
    });
    window.addEventListener("focus", refresh);
    onCleanup(() => window.removeEventListener("focus", refresh));
  });
  return () => {
    document.removeEventListener("visibilitychange", onVisibility);
    offView();
  };
}
