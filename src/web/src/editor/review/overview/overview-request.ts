import { createSignal } from "solid-js";
import type { ClientSession } from "../../../bridge";

// Show Session Overview can run before the review tab mounts; the tab settles the request once it can scroll.
const [requested, setRequested] = createSignal<ClientSession | null>(null);

/** Asks `session`'s review tab to show its overview. */
export function requestOverview(session: ClientSession): void {
  setRequested(session);
}

export const overviewRequested = (session: ClientSession): boolean => requested() === session;

export function settleOverviewRequest(session: ClientSession): void {
  if (requested() === session) setRequested(null);
}
