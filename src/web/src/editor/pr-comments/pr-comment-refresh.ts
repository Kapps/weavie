import { createEffect, createMemo, createRoot, createSignal, onCleanup } from "solid-js";
import { selectedSession } from "../../bridge";
import { prCommentsFor } from "./pr-comments-store";

// Unchanged polls are conditional requests the forge answers 304, so they cost no rate limit.
const REFRESH_MS = 60_000;

/**
 * Keeps PR comments fresh while the user can see them — the selected session, in a visible window — on selection,
 * on returning to the window, and every minute. Comments are forge state, unrelated to what the agent is doing.
 */
export function installPrCommentRefresh(): () => void {
  return createRoot((dispose) => {
    const [visible, setVisible] = createSignal(document.visibilityState === "visible");
    const onVisibility = (): void => {
      setVisible(document.visibilityState === "visible");
    };
    const watched = createMemo(() => {
      const session = selectedSession();
      return visible() && session !== null && prCommentsFor(session)?.set != null ? session : null;
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
