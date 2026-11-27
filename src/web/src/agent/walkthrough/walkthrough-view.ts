import { createSignal } from "solid-js";

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

// A stop the pane's card asked a walkthrough tab to show, by tab path; the tab takes it once.
const [requested, setRequested] = createSignal<ReadonlyMap<string, string>>(new Map());

/** Asks the tab at `path` to show stop `stopId`, now or when it opens. */
export function requestStop(path: string, stopId: string): void {
  setRequested((current) => new Map(current).set(path, stopId));
}

/** The stop the tab at `path` was asked to show; reading it takes it. */
export function takeRequestedStop(path: string): string | null {
  const stopId = requested().get(path) ?? null;
  if (stopId !== null) {
    setRequested((current) => {
      const next = new Map(current);
      next.delete(path);
      return next;
    });
  }
  return stopId;
}

/** Whether a stop was asked for at `path`, tracked reactively. */
export const stopRequested = (path: string): boolean => requested().has(path);
