import {
  defaultRangeExtractor,
  type Range,
  type VirtualItem,
  type Virtualizer,
} from "@tanstack/solid-virtual";
import {
  type Accessor,
  createComputed,
  createEffect,
  createMemo,
  createSignal,
  on,
  untrack,
} from "solid-js";
import { estimateEntrySize, invalidateEntrySize } from "./AgentPaneEstimate";
import type { AgentPaneModel } from "./AgentPaneModel";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";

interface Layout {
  generation: number;
  entries: Map<string, AgentTranscriptEntry>;
  keys: string[];
  pendingKeys: Set<string>;
}

export function createAgentPaneLayout(
  model: AgentPaneModel,
  body: Accessor<HTMLDivElement | undefined>,
) {
  const [layout, setLayout] = createSignal<Layout>({
    generation: model.generation(),
    entries: new Map(),
    keys: [],
    pendingKeys: new Set(),
  });
  let beforeChange = (_previous: Layout, _next: Layout): void => {};
  createComputed(() => {
    const generation = model.generation();
    const entries = new Map(model.entries.map((entry) => [`${generation}\0${entry.id}`, entry]));
    const keys = [...entries.keys()];
    const next = {
      generation,
      entries,
      keys,
      pendingKeys: new Set(model.pendingRowIndexes().map((index) => keys[index]!)),
    };
    untrack(() => beforeChange(layout(), next));
    setLayout(next);
  });
  const itemKey = createMemo(() => {
    const keys = layout().keys;
    return (index: number) => keys[index]!;
  });
  const rangeExtractor = createMemo(() => {
    const current = layout();
    const pending = current.keys.flatMap((key, index) =>
      current.pendingKeys.has(key) ? [index] : [],
    );
    // Pending forms own live command handlers and focus even while outside the viewport.
    return (range: Range) =>
      [...new Set([...defaultRangeExtractor(range), ...pending])].sort(
        (left, right) => left - right,
      );
  });

  return {
    count: () => layout().keys.length,
    entryForKey: (key: VirtualItem["key"]): AgentTranscriptEntry =>
      layout().entries.get(String(key))!,
    itemKey,
    rangeExtractor,
    observe(
      virtualizer: Virtualizer<HTMLDivElement, HTMLDivElement>,
      scroll: {
        followingLatest: Accessor<boolean>;
        restoreReadingPosition: (offset: number) => void;
      },
    ): void {
      let anchor: { key: string; offset: number } | null = null;
      beforeChange = (previous, next) => {
        const element = body();
        if (
          element === undefined ||
          scroll.followingLatest() ||
          previous.generation !== next.generation
        ) {
          anchor = null;
          return;
        }
        const row = virtualizer.getVirtualItemForOffset(element.scrollTop);
        anchor =
          row === undefined
            ? null
            : { key: String(row.key), offset: element.scrollTop - row.start };
      };
      createEffect(
        on(layout, (next, previous) => {
          if (previous !== undefined) {
            for (const key of previous.pendingKeys) {
              if (next.pendingKeys.has(key)) continue;
              const index = next.keys.indexOf(key);
              const entry = model.entries[index];
              if (entry === undefined) continue;
              invalidateEntrySize(entry);
              const element = virtualizer.elementsCache.get(key);
              if (element?.isConnected) virtualizer.measureElement(element);
              else virtualizer.resizeItem(index, estimateEntrySize(entry));
            }
          }
          if (anchor !== null) {
            const index = next.keys.indexOf(anchor.key);
            const row = virtualizer.measurementsCache[index];
            if (row !== undefined)
              scroll.restoreReadingPosition(
                row.start + Math.min(anchor.offset, Math.max(0, row.size - 1)),
              );
            anchor = null;
          }
        }),
      );
    },
  };
}
