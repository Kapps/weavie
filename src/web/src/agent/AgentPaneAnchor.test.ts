import type { VirtualItem, Virtualizer } from "@tanstack/solid-virtual";
import { describe, expect, it } from "vitest";
import { createReadingAnchor } from "./AgentPaneAnchor";

// Two rendered rows stacked from the bottom; neither measurement differs from its estimate.
function rows(): VirtualItem[] {
  return [
    { index: 0, key: "newest", start: 0, size: 100, end: 100, lane: 0 },
    { index: 1, key: "older", start: 100, size: 100, end: 200, lane: 0 },
  ];
}

function fakeVirtualizer(items: VirtualItem[]) {
  const connected = { isConnected: true } as HTMLDivElement;
  return {
    elementsCache: new Map(items.map((item) => [String(item.key), connected])),
    getVirtualItemForOffset: (offset: number) =>
      items.find((item) => item.start <= offset && offset < item.end),
    getVirtualItems: () => items,
    itemSizeCache: new Map<string, number>(),
    measurementsCache: items,
  } as unknown as Virtualizer<HTMLDivElement, HTMLDivElement>;
}

describe("reading anchor", () => {
  it("counts a row measured at exactly its estimate as measured", () => {
    const items = rows();
    const anchor = createReadingAnchor(
      () => fakeVirtualizer(items),
      (id) => items.find((item) => item.key === id),
    );
    expect(anchor.measuredThrough(1)).toBe(false);
    anchor.noteMeasured("newest");
    expect(anchor.measuredThrough(0)).toBe(true);
    expect(anchor.measuredThrough(1)).toBe(false);
    anchor.noteMeasured("older");
    expect(anchor.measuredThrough(1)).toBe(true);
    anchor.forget("older");
    expect(anchor.measuredThrough(1)).toBe(false);
  });

  it("absorbs a shown row's first measurement and keeps a measured row's growth", () => {
    const items = rows();
    const anchor = createReadingAnchor(
      () => fakeVirtualizer(items),
      (id) => items.find((item) => item.key === id),
    );
    anchor.pick(120, 60);
    items[0] = { ...items[0]!, size: 140, end: 140 };
    items[1] = { ...items[1]!, start: 140, end: 240 };
    anchor.noteMeasured("newest");
    expect(anchor.target()).toBe(120);
    items[0] = { ...items[0]!, size: 180, end: 180 };
    items[1] = { ...items[1]!, start: 180, end: 280 };
    expect(anchor.target()).toBe(160);
  });
});
