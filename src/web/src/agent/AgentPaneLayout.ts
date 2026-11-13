import {
  defaultRangeExtractor,
  type Range,
  type VirtualItem,
  type Virtualizer,
} from "@tanstack/solid-virtual";
import { type Accessor, createEffect, createMemo, on } from "solid-js";
import { estimateEntrySize } from "./AgentPaneEstimate";
import type { AgentTranscriptEntry, AgentTranscriptSnapshot } from "./AgentPaneTranscriptTypes";
import { entryMeasurementSource } from "./AgentTranscriptSnapshot";
import { selectingBlockEntry } from "./agent-block-selection";

/** Maps the shown transcript onto the virtualizer, which measures from the bottom: index 0 is the newest row. */
export function createAgentPaneLayout(
  shown: Accessor<AgentTranscriptSnapshot>,
  virtualizer: () => Virtualizer<HTMLDivElement, HTMLDivElement>,
  onEstimated: (id: string) => void,
  cardChanges: Accessor<readonly { id: string; delta: number }[]>,
  holding: Accessor<boolean>,
) {
  const entries = createMemo(() => shown().entries);
  const indexes = () => shown().entryIndexes;
  const flip = (index: number): number => reverse(shown().entries.length, index);
  const rowAt = (index: number | null): VirtualItem | undefined =>
    index === null ? undefined : virtualizer().measurementsCache[flip(index)];
  const itemKey = createMemo(() => {
    const rows = entries();
    return (virtualIndex: number) => rows[reverse(rows.length, virtualIndex)]!.id;
  });

  // A changed row that isn't mounted can't be measured again, so it returns to its estimate.
  createEffect(
    on(
      entries,
      (next, previous) => {
        const before = new Map(previous?.map((entry) => [entry.id, entry]));
        next.forEach((entry, index) => {
          const old = before.get(entry.id);
          if (
            old !== undefined &&
            old !== entry &&
            entryMeasurementSource(old) !== entryMeasurementSource(entry) &&
            virtualizer().elementsCache.get(entry.id)?.isConnected !== true
          ) {
            onEstimated(entry.id);
            virtualizer().resizeItem(flip(index), estimateEntrySize(entry));
          }
        });
      },
      { defer: true },
    ),
  );

  const dirtyCards = new Set<string>();
  createEffect(() => {
    for (const { id, delta } of cardChanges()) if (delta !== 0) dirtyCards.add(id);
    if (holding()) return;
    for (const id of dirtyCards) {
      const index = indexes().get(id);
      if (
        index === undefined ||
        virtualizer().elementsCache.get(id)?.isConnected === true ||
        rowAt(index) === undefined
      )
        continue;
      onEstimated(id);
      virtualizer().resizeItem(flip(index), estimateEntrySize(shown().entries[index]));
    }
    dirtyCards.clear();
  });

  return {
    count: () => shown().entries.length,
    entryAt: (virtualIndex: number): AgentTranscriptEntry | undefined =>
      shown().entries[flip(virtualIndex)],
    entryForKey: (key: VirtualItem["key"]): AgentTranscriptEntry | undefined => {
      const index = indexes().get(String(key));
      return index === undefined ? undefined : shown().entries[index];
    },
    /** The measured row at transcript `index`. */
    rowAt,
    /** The measured row of the entry with `id`, if the shown transcript has it. */
    rowOf: (id: string): VirtualItem | undefined => rowAt(indexes().get(id) ?? null),
    itemKey,
    // Pending forms own live command handlers, and a block mid-gesture owns its selection, so they stay mounted.
    rangeExtractor: createMemo(() => {
      const { entries, entryIndexes, pendingRowIndexes } = shown();
      const selecting = entryIndexes.get(selectingBlockEntry() ?? "");
      const pending = [...pendingRowIndexes, ...(selecting === undefined ? [] : [selecting])].map(
        (index) => reverse(entries.length, index),
      );
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
