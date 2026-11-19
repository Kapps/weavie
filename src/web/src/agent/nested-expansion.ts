import { type Accessor, createSignal } from "solid-js";
import type { ClientSession } from "../bridge";
import { agentPaneModel } from "./pane-store";

/** `current` with `entryId` in or out of it, or `current` itself when nothing changes. */
export function toggleMember(
  current: ReadonlySet<string>,
  entryId: string,
  included: boolean,
): ReadonlySet<string> {
  if (current.has(entryId) === included) {
    return current;
  }
  const next = new Set(current);
  if (included) {
    next.add(entryId);
  } else {
    next.delete(entryId);
  }
  return next;
}

/** What a transcript shown outside the pane has open: its own details, and the session's update groups. */
export function createNestedExpansion(session: ClientSession): {
  expandedDetails: Accessor<ReadonlySet<string>>;
  onDetailsToggle(entryId: string, open: boolean): void;
  onUpdatesToggle(entryId: string, open: boolean): void;
} {
  const [expandedDetails, setExpandedDetails] = createSignal<ReadonlySet<string>>(new Set());
  return {
    expandedDetails,
    onDetailsToggle: (entryId, open) =>
      setExpandedDetails((current) => toggleMember(current, entryId, open)),
    onUpdatesToggle: (entryId, open) => agentPaneModel(session)!.setActivityExpanded(entryId, open),
  };
}
