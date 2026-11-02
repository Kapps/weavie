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
  generation: Accessor<number>,
  anchorId: () => string | null,
  virtualizer: () => Virtualizer<HTMLDivElement, HTMLDivElement>,
) {
  const layout = createMemo(() => {
    const { entries, pendingRowIndexes } = shown();
    const prefix = `${generation()}\0`;
    const keys = entries.map((entry) => prefix + entry.id);
    return {
      entries,
      keys,
      indexes: new Map(keys.map((key, index) => [key, index])),
      pendingRowIndexes,
      prefix,
    };
  });
  const flip = (index: number): number => reverse(layout().entries.length, index);
  const itemKey = createMemo(() => {
    const { keys } = layout();
    return (virtualIndex: number) => keys[reverse(keys.length, virtualIndex)]!;
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
            virtualizer().elementsCache.get(itemKey()(row))?.isConnected !== true
          ) {
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
    entryForKey: (key: VirtualItem["key"]): AgentTranscriptEntry =>
      layout().entries[layout().indexes.get(String(key))!]!,
    flip,
    itemKey,
    // Pending forms own live command handlers, and the reader's anchor must stay measurable.
    rangeExtractor: createMemo(() => {
      const { indexes, keys, pendingRowIndexes, prefix } = layout();
      const pending = pendingRowIndexes.map((index) => reverse(keys.length, index));
      return (range: Range) => {
        const id = anchorId();
        const anchor = id === null ? undefined : indexes.get(prefix + id);
        const extra = anchor === undefined ? pending : [...pending, reverse(keys.length, anchor)];
        return [...new Set([...defaultRangeExtractor(range), ...extra])].sort(
          (left, right) => left - right,
        );
      };
    }),
  };
}

// The one place transcript order and virtual order meet; the mapping is its own inverse.
function reverse(count: number, index: number): number {
  return count - 1 - index;
}
