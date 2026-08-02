import { describe, expect, it } from "vitest";
import { placeRailPopover } from "./popover-position";

describe("placeRailPopover", () => {
  it("top-aligns below a near-top rail anchor instead of clipping above the viewport", () => {
    expect(
      placeRailPopover(
        { left: 8, right: 36, top: 48, bottom: 76 },
        { width: 230, height: 420 },
        { width: 1280, height: 800 },
      ),
    ).toEqual({ left: 42, top: 48 });
  });

  it("bottom-aligns to a lower rail anchor", () => {
    expect(
      placeRailPopover(
        { left: 8, right: 36, top: 680, bottom: 708 },
        { width: 230, height: 420 },
        { width: 1280, height: 800 },
      ),
    ).toEqual({ left: 42, top: 288 });
  });

  it("flips horizontally and clamps vertically inside the viewport inset", () => {
    expect(
      placeRailPopover(
        { left: 960, right: 988, top: 4, bottom: 32 },
        { width: 230, height: 584 },
        { width: 1000, height: 600 },
      ),
    ).toEqual({ left: 724, top: 8 });
  });
});
