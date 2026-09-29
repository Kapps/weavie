import { defaultRangeExtractor } from "@tanstack/solid-virtual";
import { createComputed, createMemo, createRoot, createSignal } from "solid-js";
import { describe, expect, it, onTestFinished, vi } from "vitest";
import { createReviewLayout } from "./review-layout";

vi.mock("solid-js", () => import(["solid-js", "dist/solid.js"].join("/")));

function fixture(retain: boolean, count: number) {
  let top = 0;
  let extent = 0;
  let offset: ((value: number, scrolling: boolean) => void) | undefined;
  let rect: ((value: { width: number; height: number }) => void) | undefined;
  let offsetSubscriptions = 0;
  let offsetRemovals = 0;
  let rectRemovals = 0;
  let optionsReads = 0;
  const frames = new Map<number, FrameRequestCallback>();
  let frame = 0;
  const viewportWindow = {
    requestAnimationFrame: (callback: FrameRequestCallback) => {
      frames.set(++frame, callback);
      return frame;
    },
    cancelAnimationFrame: (id: number) => frames.delete(id),
    setTimeout,
    clearTimeout,
  };
  const element = () =>
    ({
      ownerDocument: { defaultView: viewportWindow },
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      get scrollHeight() {
        return extent;
      },
      clientHeight: 100,
    }) as unknown as HTMLElement;
  return createRoot((dispose) => {
    onTestFinished(dispose);
    const [scrollElement, setElement] = createSignal(element());
    const [keys, setKeys] = createSignal(
      Array.from({ length: count }, (_, index) => `row-${index}`),
    );
    const [unrelated, setUnrelated] = createSignal(0);
    const itemKey = createMemo(() => {
      const ordered = keys();
      return (index: number) => ordered[index]!;
    });
    const layout = createReviewLayout(() => {
      optionsReads++;
      return {
        count: keys().length,
        getItemKey: itemKey(),
        getScrollElement: scrollElement,
        estimateSize: () => 50.25,
        gap: 2.5,
        rangeExtractor: retain
          ? (range) => Array.from({ length: range.count }, (_, index) => index)
          : defaultRangeExtractor,
        observeElementRect: (_instance, callback) => {
          rect = callback;
          callback({ width: 700, height: 100 });
          return () => rectRemovals++;
        },
        observeElementOffset: (_instance, callback) => {
          offsetSubscriptions++;
          offset = callback;
          callback(top, false);
          return () => offsetRemovals++;
        },
        scrollToFn: (value, settings, instance) => {
          extent = instance.getTotalSize();
          top = Math.max(
            0,
            Math.min(
              extent - 100,
              settings.adjustments === undefined ? value : top + settings.adjustments,
            ),
          );
          offset?.(top, false);
        },
        onChange: (instance) => {
          unrelated();
          extent = instance.getTotalSize();
        },
      };
    });
    let rowUpdates = 0;
    let sizeUpdates = 0;
    createComputed(() => {
      layout.rows();
      rowUpdates++;
    });
    createComputed(() => {
      layout.totalSize();
      sizeUpdates++;
    });
    return {
      layout,
      setKeys,
      setUnrelated,
      dispose,
      replaceElement: () => setElement(element()),
      resizeViewport: (height: number) => rect!({ width: 700, height }),
      rectCallback: () => rect!,
      move: (value: number) => {
        top = value;
        offset!(value, false);
      },
      state: () => ({
        top,
        extent,
        rowUpdates,
        sizeUpdates,
        optionsReads,
        offsetSubscriptions,
        offsetRemovals,
        rectRemovals,
      }),
    };
  });
}

describe("review layout publication", () => {
  it("keeps 105 retained rows stable while core offset and range advance", () => {
    const f = fixture(true, 105);
    const rows = f.layout.rows();
    const before = f.state();
    for (let top = 0; top <= 3000; top++) f.move(top);
    expect(f.layout.instance.scrollOffset).toBe(3000);
    expect(f.layout.instance.range!.startIndex).toBeGreaterThan(50);
    expect(f.layout.rows()).toBe(rows);
    expect(f.state().rowUpdates).toBe(before.rowUpdates);
    expect(f.state().sizeUpdates).toBe(before.sizeUpdates);
    expect(f.state().offsetSubscriptions).toBe(1);
  });

  it("preserves fractional resize compensation and historical snapshots", () => {
    const f = fixture(true, 10);
    const before = f.layout.rows();
    f.move(200);
    f.layout.instance.resizeItem(0, 90.75);
    expect(f.state().top).toBe(240.5);
    expect(f.layout.rows()[1]!.start).toBe(93.25);
    expect(f.layout.totalSize()).toBe(565.5);
    expect(before[0]!.size).toBe(50.25);
    expect(before[1]!.start).toBe(52.75);
    expect(f.layout.rows()[0]).not.toBe(before[0]);
    expect(f.layout.totalSize()).toBe(f.layout.instance.getTotalSize());
    expect(f.state().extent).toBe(f.layout.totalSize());
  });

  it("updates tree, within-viewport and below-viewport sizes without losing collapse geometry", () => {
    const f = fixture(true, 10);
    f.move(120);
    f.layout.instance.resizeItem(0, 20.125);
    expect(f.state().top).toBe(89.875);
    f.layout.instance.resizeItem(2, 70.75);
    const afterInitialMeasure = f.state().top;
    f.layout.instance.resizeItem(2, 85.5);
    expect(f.state().top).toBe(afterInitialMeasure);
    f.layout.instance.resizeItem(8, 100.5);
    expect(f.state().top).toBe(afterInitialMeasure);
    f.layout.instance.resizeItem(3, 0);
    expect(f.layout.rows()[3]!.size).toBe(0);
    f.layout.instance.resizeItem(3, 200.25);
    expect(f.layout.rows()[3]!.size).toBe(200.25);
    expect(f.layout.totalSize()).toBe(f.layout.instance.getTotalSize());
  });

  it("reorders and replaces same-count keys while retaining measurements by key", () => {
    const f = fixture(true, 4);
    f.layout.instance.resizeItem(1, 100.125);
    f.setKeys(["row-0", "row-2", "row-1", "replacement"]);
    expect(f.layout.rows().map((row) => row.key)).toEqual([
      "row-0",
      "row-2",
      "row-1",
      "replacement",
    ]);
    expect(f.layout.rows()[2]!.size).toBe(100.125);
    expect(f.layout.rows()[2]!.start).toBe(105.5);
    expect(f.layout.instance.itemSizeCache.get("row-1")).toBe(100.125);
  });

  it("uses current core geometry for navigation through pending measurement changes", () => {
    const f = fixture(true, 15);
    f.layout.instance.resizeItem(1, 200.75);
    f.layout.instance.scrollToIndex(8, { align: "start" });
    expect(f.state().top).toBe(f.layout.rows()[8]!.start);
    expect(f.state().extent).toBe(f.layout.instance.getTotalSize());
  });

  it("publishes default virtual ranges as scrolling and viewport size change", () => {
    const f = fixture(false, 105);
    const before = f.layout.rows().map((row) => row.index);
    f.move(1000);
    const after = f.layout.rows().map((row) => row.index);
    expect(after[0]).toBeGreaterThan(before.at(-1)!);
    expect(after.length).toBeLessThan(10);
    f.resizeViewport(500);
    expect(f.layout.rows().length).toBeGreaterThan(after.length);
  });

  it("publishes viewport resize without replacing retained rows or accepting retired observers", () => {
    const f = fixture(true, 105);
    const rows = f.layout.rows();
    const before = f.state();
    const range = { ...f.layout.instance.range };
    f.resizeViewport(100.5);
    expect(f.layout.viewportHeight()).toBe(100.5);
    expect(f.layout.instance.range).toEqual(range);
    expect(f.layout.rows()).toBe(rows);
    expect(f.state().rowUpdates).toBe(before.rowUpdates);
    const oldRect = f.rectCallback();
    f.replaceElement();
    expect(f.layout.viewportHeight()).toBe(100);
    oldRect({ width: 700, height: 900 });
    expect(f.layout.viewportHeight()).toBe(100);
    expect(f.layout.instance.scrollRect?.height).toBe(100);
    f.dispose();
    expect(f.layout.viewportHeight()).toBeUndefined();
    expect(f.state().rectRemovals).toBe(2);
  });

  it("anchors preceding measurements until navigation yields to user scrolling", () => {
    const f = fixture(true, 10);
    const fileIndex = 3;
    f.layout.instance.shouldAdjustScrollPositionOnItemSizeChange = (item) =>
      item.index <= fileIndex;
    f.layout.instance.scrollToIndex(fileIndex + 1, { align: "start" });
    f.layout.instance.resizeItem(2, 110.75);
    expect(f.state().top).toBe(f.layout.rows()[fileIndex + 1]!.start);
    const top = f.state().top;
    f.layout.instance.resizeItem(fileIndex + 1, 160);
    expect(f.state().top).toBe(top);
    f.layout.instance.shouldAdjustScrollPositionOnItemSizeChange = undefined;
    f.move(0);
    f.layout.instance.resizeItem(2, 200);
    expect(f.state().top).toBe(0);
  });

  it("does not subscribe options to imperative notifications and cleans up replaced elements", () => {
    const f = fixture(true, 10);
    f.move(300);
    const before = f.state();
    f.setUnrelated(1);
    expect(f.state().optionsReads).toBe(before.optionsReads);
    f.replaceElement();
    expect(f.state().offsetSubscriptions).toBe(2);
    expect(f.state().offsetRemovals).toBe(1);
    expect(f.state().rectRemovals).toBe(1);
    f.dispose();
    expect(f.state().offsetRemovals).toBe(2);
    expect(f.state().rectRemovals).toBe(2);
  });

  it("keeps a reentrant resize coherent with the most recent published geometry", () => {
    const f = fixture(true, 10);
    createRoot((dispose) => {
      onTestFinished(dispose);
      createComputed(() => {
        if (f.layout.rows()[0]!.size === 75.5 && f.layout.rows()[1]!.size === 50.25)
          f.layout.instance.resizeItem(1, 95.75);
      });
    });
    f.layout.instance.resizeItem(0, 75.5);
    expect(f.layout.rows()[1]!.size).toBe(95.75);
    expect(f.layout.rows()[2]!.start).toBe(176.25);
    expect(f.layout.totalSize()).toBe(f.layout.instance.getTotalSize());
    expect(f.state().extent).toBe(f.layout.totalSize());
  });
});
