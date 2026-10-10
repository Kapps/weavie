import { describe, expect, it } from "vitest";
import { createPanZoom } from "./pan-zoom";

const frame = { getBoundingClientRect: () => ({ left: 0, top: 0, width: 200, height: 100 }) };
const make = () => createPanZoom(() => frame as unknown as HTMLElement);

describe("createPanZoom", () => {
  it("zooms with the keys, clamps to 1×–8×, and resets", () => {
    const zoom = make();
    expect(zoom.zoomKey("+")).toBe(true);
    expect(zoom.scale()).toBeCloseTo(1.25);
    for (let i = 0; i < 20; i++) zoom.zoomKey("=");
    expect(zoom.scale()).toBe(8);
    expect(zoom.zoomKey("0")).toBe(true);
    expect(zoom.scale()).toBe(1);
    zoom.zoomKey("-");
    expect(zoom.scale()).toBe(1);
    expect(zoom.zoomKey("x")).toBe(false);
  });

  it("keeps the point under the cursor fixed while zooming toward it", () => {
    const zoom = make();
    zoom.handlers.onDblClick({ clientX: 150, clientY: 50 } as MouseEvent);
    // The frame center is (100, 50); doubling about x=150 shifts content left by 50px.
    expect(zoom.scale()).toBe(2);
    expect(zoom.transform()).toBe("translate(-50px, 0px) scale(2)");
  });

  it("pinches toward the fingers, then pans with the one left down", () => {
    const zoom = make();
    const target = { setPointerCapture: () => {} };
    const ev = (pointerId: number, clientX: number, clientY: number) =>
      ({ pointerId, clientX, clientY, pointerType: "touch", currentTarget: target }) as never;
    zoom.handlers.onPointerDown(ev(1, 90, 50));
    zoom.handlers.onPointerDown(ev(2, 110, 50));
    zoom.handlers.onPointerMove(ev(2, 130, 50)); // spread 20 → 40 around the moving midpoint
    expect(zoom.scale()).toBe(2);
    zoom.handlers.onPointerUp(ev(2, 130, 50));
    const before = zoom.transform();
    zoom.handlers.onPointerMove(ev(1, 100, 50));
    expect(zoom.panning()).toBe(true);
    expect(zoom.transform()).not.toBe(before);
    zoom.handlers.onPointerUp(ev(1, 100, 50));
    expect(zoom.panning()).toBe(false);
  });
});
