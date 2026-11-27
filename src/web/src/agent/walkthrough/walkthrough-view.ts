import { createSignal } from "solid-js";
import type { ClientSession } from "../../bridge";

/** What a walkthrough tab saves with its tab. */
export interface WalkthroughViewState {
  stopId: string | null;
  read: string[];
  openFolds: string[];
  revealed: string[];
}

export const emptyView = (): WalkthroughViewState => ({
  stopId: null,
  read: [],
  openFolds: [],
  revealed: [],
});

/** Where the pane's card asks a walkthrough tab to go: a stop, or a question's card. */
export type WalkthroughTarget = { stopId: string } | { question: string };

// Requests by session, then tab path; a tab takes its own once, now or when it opens.
const [requested, setRequested] = createSignal<
  ReadonlyMap<ClientSession, ReadonlyMap<string, WalkthroughTarget>>
>(new Map());

/** Asks `session`'s walkthrough tab at `path` to show `target`. */
export function requestTarget(
  session: ClientSession,
  path: string,
  target: WalkthroughTarget,
): void {
  setRequested((current) => {
    const next = new Map(current);
    next.set(session, new Map(current.get(session)).set(path, target));
    return next;
  });
}

/** The target asked of the tab, tracked reactively; reading it with `take` removes it. */
export function requestedTarget(
  session: ClientSession,
  path: string,
  take: boolean,
): WalkthroughTarget | null {
  const target = requested().get(session)?.get(path) ?? null;
  if (target !== null && take) {
    setRequested((current) => {
      const next = new Map(current);
      const paths = new Map(current.get(session));
      paths.delete(path);
      next.set(session, paths);
      return next;
    });
  }
  return target;
}
