import { describe, expect, it } from "vitest";
import { keepFold } from "./review-fold";

// The view is at 1000; a file's real height arriving mid-scroll moves the view with it only when it's above.
describe("keepFold", () => {
  const list = (measured: string[]) => ({
    scrollOffset: 1000,
    scrollAdjustments: 0,
    itemSizeCache: new Map(measured.map((key) => [key, 100])),
  });

  it("moves the view for a re-measured file wholly above it, whichever way the reader scrolls", () => {
    expect(keepFold({ key: "a", start: 200, end: 900 }, 800, list(["a"]))).toBe(true);
  });

  it("leaves the view for a re-measured file spanning it, which grows below what's on screen", () => {
    expect(keepFold({ key: "a", start: 800, end: 1300 }, 800, list(["a"]))).toBe(false);
  });

  it("moves the view for a first measure of a file starting above it", () => {
    expect(keepFold({ key: "a", start: 800, end: 1300 }, 800, list([]))).toBe(true);
  });
});
