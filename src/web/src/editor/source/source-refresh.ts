import type { ClientSession } from "../../bridge";
import { refreshSourceDoc } from "./source-store";

/** Refreshes a displayed source; each request finishes before another can start. */
export function watchSourceDoc(session: ClientSession, target: string): () => void {
  const lifetime = new AbortController();
  let fetching = false;
  const refresh = async (): Promise<void> => {
    if (fetching || document.hidden || lifetime.signal.aborted || session.closed) return;
    fetching = true;
    try {
      await refreshSourceDoc(session, target, lifetime.signal);
    } finally {
      fetching = false;
    }
  };
  const timer = window.setInterval(() => void refresh(), 15_000);
  window.addEventListener("focus", refresh);
  document.addEventListener("visibilitychange", refresh);
  void refresh();
  return () => {
    lifetime.abort();
    window.clearInterval(timer);
    window.removeEventListener("focus", refresh);
    document.removeEventListener("visibilitychange", refresh);
  };
}
