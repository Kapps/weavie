import type { ClientSession } from "../../bridge";
import { createSessionOwnedState } from "../../messaging/session-owned-state";

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

// Requests by tab path; a tab takes its own once, now or when it opens.
const requested = createSessionOwnedState<ReadonlyMap<string, WalkthroughTarget>>(() => new Map());

/** Asks `session`'s walkthrough tab at `path` to show `target`. */
export function requestTarget(
  session: ClientSession,
  path: string,
  target: WalkthroughTarget,
): void {
  requested.update(session, (current) => new Map(current).set(path, target));
}

/** The target asked of the tab, tracked reactively; reading it with `take` removes it. */
export function requestedTarget(
  session: ClientSession,
  path: string,
  take: boolean,
): WalkthroughTarget | null {
  const target = requested.get(session)?.get(path) ?? null;
  if (target !== null && take) {
    requested.update(session, (current) => {
      const next = new Map(current);
      next.delete(path);
      return next;
    });
  }
  return target;
}
