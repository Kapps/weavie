import type { VirtualItem, Virtualizer } from "@tanstack/solid-virtual";
import { createRoot, createSignal } from "solid-js";
import { describe, expect, it, vi } from "vitest";
import { createAgentPaneLayout } from "./AgentPaneLayout";
import type { AgentTranscriptEntry, AgentTranscriptSnapshot } from "./AgentPaneTranscriptTypes";
import { emptyTranscript } from "./AgentTranscriptSnapshot";

vi.mock(
  "solid-js",
  async () => await vi.importActual<typeof import("solid-js")>("solid-js/dist/solid.js"),
);

describe("agent pane layout", () => {
  it("reconciles offscreen card size to the current projection after a hold", () => {
    const heights = [100, 100];
    const cards = heights.map(
      (_, index) =>
        ({
          id: `child-${index}`,
          conversation: {
            get estimatedHeight() {
              return heights[index]!;
            },
          },
        }) as AgentTranscriptEntry,
    );
    const shown = {
      ...emptyTranscript,
      entries: cards,
      entryIndexes: new Map(cards.map((card, index) => [card.id, index])),
    };
    const rows = heights.map((size, index) => ({ index, size }) as VirtualItem);
    const resizeItem = vi.fn((index: number, size: number) => {
      rows[index]!.size = size;
    });
    const controls = createRoot((dispose) => {
      const [holding, setHolding] = createSignal(false);
      const [changes, setChanges] = createSignal<readonly { id: string; delta: number }[]>([]);
      createAgentPaneLayout(
        () => shown,
        () =>
          ({
            measurementsCache: rows,
            elementsCache: new Map(),
            resizeItem,
          }) as unknown as Virtualizer<HTMLDivElement, HTMLDivElement>,
        () => {},
        changes,
        holding,
      );
      return { dispose, setHolding, setChanges };
    });
    controls.setHolding(true);
    heights[0]! += 10;
    controls.setChanges([{ id: cards[0]!.id, delta: 10 }]);
    heights[1]! += 20;
    controls.setChanges([{ id: cards[1]!.id, delta: 20 }]);
    expect(rows.map((row) => row.size)).toEqual([100, 100]);
    controls.setHolding(false);
    expect(rows.map((row) => row.size)).toEqual([120, 110]);
    expect(resizeItem).toHaveBeenCalledTimes(2);
    controls.dispose();
  });

  it("measures from the bottom: virtual index 0 is the newest entry", () => {
    createRoot((dispose) => {
      const measurementsCache = [0, 1, 2, 3, 4, 5].map((index) => ({ index }) as VirtualItem);
      const virtualizer = { measurementsCache } as Virtualizer<HTMLDivElement, HTMLDivElement>;
      const layout = createAgentPaneLayout(
        () => transcript(["a", "b", "c", "d", "e", "f"], [5]),
        () => virtualizer,
        () => {},
        () => [],
        () => false,
      );
      expect(layout.count()).toBe(6);
      expect([0, 5, null].map((index) => layout.rowAt(index)?.index)).toEqual([5, 0, undefined]);
      expect(layout.entryAt(0)?.id).toBe("f");
      expect(layout.entryAt(5)?.id).toBe("a");
      expect(layout.entryAt(6)).toBeUndefined();
      expect(layout.itemKey()(0)).toBe("f");
      expect(layout.entryForKey("b")?.id).toBe("b");
      expect(layout.entryForKey("missing")).toBeUndefined();
      expect(layout.rowOf("b")?.index).toBe(4);
      expect(layout.rowOf("missing")).toBeUndefined();

      const range = { startIndex: 2, endIndex: 3, overscan: 0, count: 6 };
      expect(layout.rangeExtractor()(range)).toEqual([0, 2, 3]);
      dispose();
    });
  });
});

function transcript(ids: string[], pendingRowIndexes: number[]): AgentTranscriptSnapshot {
  return {
    ...emptyTranscript,
    entries: ids.map((id) => ({ id }) as AgentTranscriptEntry),
    pendingRowIndexes,
    entryIndexes: new Map(ids.map((id, index) => [id, index])),
  };
}
