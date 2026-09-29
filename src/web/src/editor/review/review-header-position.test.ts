import { afterEach, describe, expect, it, vi } from "vitest";
import { createReviewHeaderPosition, measureReviewHeader } from "./review-header-position";

afterEach(() => {
  vi.unstubAllGlobals();
});

function fixture() {
  const writes: string[] = [];
  const style = {
    get top(): string {
      throw new Error("Header placement must not read CSSOM");
    },
    set top(value: string) {
      writes.push(value);
    },
    position: "absolute",
  };
  const header = { style } as unknown as HTMLElement;
  return { style, writes, place: createReviewHeaderPosition(header) };
}

describe("owned review header placement", () => {
  it("writes initial zero and avoids CSSOM reads and writes while clamped", () => {
    const { place, writes } = fixture();
    for (let top = 0; top < 1000; top++) place(top, 1000, 0.5, 500);
    expect(writes).toEqual(["0px"]);
    place(1100.75, 1000, 0.5, 500);
    place(1500.5, 1000, 0.5, 500);
    for (let top = 1501; top < 5000; top++) place(top, 1000, 0.5, 500);
    expect(writes).toEqual(["0px", "100.25px", "500px"]);
    place(1200.25, 1000, 0.5, 500);
    place(0, 1000, 0.5, 500);
    expect(writes.slice(-2)).toEqual(["199.75px", "0px"]);
  });

  it("updates for resize, reposition, border, collapse and expand at fixed scroll", () => {
    const { place, writes } = fixture();
    place(1200.75, 1000, 0.5, 500);
    place(1200.75, 1000, 0.5, 120.125);
    place(1200.75, 1100.5, 0.5, 120.125);
    place(1200.75, 1100.5, 0.25, 120.125);
    place(1200.75, 1100.5, 0.25, -0.5);
    place(1200.75, 1100.5, 0.25, 300);
    expect(writes).toEqual(["200.25px", "120.125px", "99.75px", "100px", "0px", "100px"]);
  });

  it("does not share numeric state between headers", () => {
    const first = fixture();
    const second = fixture();
    first.place(10, 0, 0, 20);
    second.place(10, 0, 0, 20);
    first.place(10, 0, 0, 20);
    expect(first.writes).toEqual(["10px"]);
    expect(second.writes).toEqual(["10px"]);
  });

  it("retains fractional border and header measurements", () => {
    vi.stubGlobal("getComputedStyle", () => ({
      borderTopWidth: "0.5px",
      borderBottomWidth: "0.75px",
    }));
    const article = { getBoundingClientRect: () => ({ height: 540.125 }) } as HTMLElement;
    const header = { getBoundingClientRect: () => ({ height: 33.25 }) } as HTMLElement;
    expect(measureReviewHeader(article, header)).toEqual({ borderTop: 0.5, limit: 505.625 });
  });
});
