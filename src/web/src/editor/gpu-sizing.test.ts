import { observeDevicePixelDimensions } from "@codingame/monaco-vscode-api/vscode/vs/editor/browser/gpu/gpuUtils";
import { describe, expect, it, vi } from "vitest";

function sizingHarness(exactPixels: boolean) {
  let deliver: ResizeObserverCallback;
  const observe = vi.fn();
  const unobserve = vi.fn();
  const disconnect = vi.fn();
  const queries: { query: string; target: EventTarget }[] = [];
  const parent = {
    devicePixelRatio: 2,
    ResizeObserverEntry: { prototype: exactPixels ? { devicePixelContentBoxSize: [] } : {} },
    ResizeObserver: class {
      constructor(callback: ResizeObserverCallback) {
        deliver = callback;
      }
      observe = observe;
      unobserve = unobserve;
      disconnect = disconnect;
    },
    matchMedia(query: string) {
      const target = new EventTarget();
      queries.push({ query, target });
      return target;
    },
  };
  const element = {} as HTMLElement;
  const callback = vi.fn();
  const subscription = observeDevicePixelDimensions(
    element,
    parent as unknown as Window & typeof globalThis,
    callback,
  );
  return {
    parent,
    queries,
    query(index: number) {
      const query = queries[index];
      if (query === undefined) throw new Error(`Missing resolution query ${index}`);
      return query;
    },
    observe,
    unobserve,
    disconnect,
    callback,
    subscription,
    deliver(width: number, height: number, physicalWidth: number, physicalHeight: number) {
      deliver(
        [
          {
            target: element,
            contentRect: { width, height },
            devicePixelContentBoxSize: [{ inlineSize: physicalWidth, blockSize: physicalHeight }],
          } as unknown as ResizeObserverEntry,
        ],
        {} as ResizeObserver,
      );
    },
  };
}

describe("patched Monaco GPU sizing", () => {
  it("uses exact physical pixels when available", () => {
    const h = sizingHarness(true);
    expect(h.observe).toHaveBeenCalledWith(expect.any(Object), { box: "device-pixel-content-box" });
    h.deliver(100.25, 50.25, 201, 101);
    expect(h.callback).toHaveBeenLastCalledWith(201, 101);
    expect(h.queries).toHaveLength(0);
    h.subscription.dispose();
    expect(h.disconnect).toHaveBeenCalledOnce();
  });

  it("sizes WebKit canvases through CSS dimensions and reobserves on each DPR change", () => {
    const h = sizingHarness(false);
    expect(h.observe).toHaveBeenCalledWith(expect.any(Object), { box: "content-box" });
    h.deliver(100.25, 50.25, 0, 0);
    expect(h.callback).toHaveBeenLastCalledWith(201, 101);
    h.parent.devicePixelRatio = 1.25;
    h.query(0).target.dispatchEvent(new Event("change"));
    expect(h.unobserve).toHaveBeenCalledOnce();
    expect(h.observe).toHaveBeenCalledTimes(2);
    expect(h.query(1).query).toBe("(resolution: 1.25dppx)");
    h.deliver(100.25, 50.25, 0, 0);
    expect(h.callback).toHaveBeenLastCalledWith(125, 63);
    h.parent.devicePixelRatio = 1;
    h.query(1).target.dispatchEvent(new Event("change"));
    h.deliver(100.25, 50.25, 0, 0);
    expect(h.callback).toHaveBeenLastCalledWith(100, 50);
    h.query(0).target.dispatchEvent(new Event("change"));
    expect(h.observe).toHaveBeenCalledTimes(3);
    h.subscription.dispose();
    h.query(2).target.dispatchEvent(new Event("change"));
    expect(h.observe).toHaveBeenCalledTimes(3);
    expect(h.disconnect).toHaveBeenCalledOnce();
  });

  it("does not resize the GPU canvas to zero while hidden", () => {
    const h = sizingHarness(false);
    h.deliver(0, 0, 0, 0);
    expect(h.callback).not.toHaveBeenCalled();
    h.deliver(120, 60, 0, 0);
    expect(h.callback).toHaveBeenLastCalledWith(240, 120);
    h.subscription.dispose();
  });
});
