import { afterEach, describe, expect, it, vi } from "vitest";
import type { ReviewScroll } from "./review-scroll";
import { routeReviewWheel } from "./review-wheel";

afterEach(() => vi.unstubAllGlobals());

describe("shared passive and live review wheel routing", () => {
  it.each([
    [0, 30, false, 0, 0, 1],
    [20, 30, false, 0, 20, 1],
    [0, 30, true, 0, 30, 0],
    [2, 0, false, 1, 44, 0],
    [1, 0, false, 2, 700, 0],
  ])("routes (%s, %s), shift=%s, units=%s exactly once", (x, y, shift, mode, dx, vertical) => {
    vi.stubGlobal("WheelEvent", { DOM_DELTA_LINE: 1, DOM_DELTA_PAGE: 2 });
    const event = {
      deltaX: x,
      deltaY: y,
      shiftKey: shift,
      deltaMode: mode,
      stopPropagation: vi.fn(),
      preventDefault: vi.fn(),
    } as unknown as WheelEvent;
    const scroll = { wheel: vi.fn() };
    const horizontal = vi.fn();
    routeReviewWheel(event, scroll as unknown as ReviewScroll, 22, 700, horizontal);
    expect(scroll.wheel).toHaveBeenCalledTimes(vertical as number);
    if (dx === 0) expect(horizontal).not.toHaveBeenCalled();
    else expect(horizontal).toHaveBeenCalledExactlyOnceWith(dx);
    expect(event.stopPropagation).toHaveBeenCalledOnce();
    expect(event.preventDefault).toHaveBeenCalledTimes(vertical === 0 ? 1 : 0);
  });
});
