import {
  defaultRangeExtractor,
  type Range,
  type VirtualItem,
  type Virtualizer,
} from "@tanstack/solid-virtual";
import { type Accessor, createEffect, createMemo, on } from "solid-js";
import { estimateEntrySize } from "./AgentPaneEstimate";
import type { AgentTranscriptEntry, AgentTranscriptSnapshot } from "./AgentPaneTranscriptTypes";

/** Maps the shown transcript onto the virtualizer, which measures from the bottom: index 0 is the newest row. */
export function createAgentPaneLayout(
  shown: Accessor<AgentTranscriptSnapshot>,
  virtualizer: () => Virtualizer<HTMLDivElement, HTMLDivElement>,
  onEstimated: (id: string) => void,
) {
  const layout = createMemo(() => {
    const { entries, pendingRowIndexes } = shown();
    return {
      entries,
      indexes: new Map(entries.map((entry, index) => [entry.id, index])),
      pendingRowIndexes,
    };
  });
  const flip = (index: number): number => reverse(layout().entries.length, index);
  const itemKey = createMemo(() => {
    const { entries } = layout();
    return (virtualIndex: number) => entries[reverse(entries.length, virtualIndex)]!.id;
  });

  // A changed row that isn't mounted can't be measured again, so it returns to its estimate.
  createEffect(
    on(
      shown,
      (next, previous) => {
        const before = new Map(previous?.entries.map((entry) => [entry.id, entry]));
        next.entries.forEach((entry, index) => {
          const old = before.get(entry.id);
          const row = flip(index);
          if (
            old !== undefined &&
            old !== entry &&
            virtualizer().elementsCache.get(entry.id)?.isConnected !== true
          ) {
            onEstimated(entry.id);
            virtualizer().resizeItem(row, estimateEntrySize(entry));
          }
        });
      },
      { defer: true },
    ),
  );

  return {
    count: () => layout().entries.length,
    entryAt: (virtualIndex: number): AgentTranscriptEntry | undefined =>
      layout().entries[flip(virtualIndex)],
    entryForKey: (key: VirtualItem["key"]): AgentTranscriptEntry | undefined => {
      const index = layout().indexes.get(String(key));
      return index === undefined ? undefined : layout().entries[index];
    },
    /** The virtual index of the row with `id`, if the shown transcript has it. */
    virtualIndexOf: (id: string): number | undefined => {
      const index = layout().indexes.get(id);
      return index === undefined ? undefined : flip(index);
    },
    flip,
    itemKey,
    // Pending forms own live command handlers, so they stay mounted.
    rangeExtractor: createMemo(() => {
      const { entries, pendingRowIndexes } = layout();
      const pending = pendingRowIndexes.map((index) => reverse(entries.length, index));
      return (range: Range) =>
        [...new Set([...defaultRangeExtractor(range), ...pending])].sort(
          (left, right) => left - right,
        );
    }),
  };
}

// The one place transcript order and virtual order meet; the mapping is its own inverse.
function reverse(count: number, index: number): number {
  return count - 1 - index;
}
