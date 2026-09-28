import { describe, expect, it, vi } from "vitest";
import { createReviewLineGeometry, reviewLineAtOffset } from "./review-line-geometry";

describe("review line geometry", () => {
  it("samples hidden ranges once and preserves the last wrap position of the previous shown line", () => {
    const tops = [12, 12, 12, 28, 71, 71, 71, 71, 130, 147, 147];
    const read = vi.fn((line: number) => tops[line - 1]!);
    const geometry = createReviewLineGeometry(tops.length, [3, 4, 9, 10], read);
    expect(read.mock.calls.flat()).toEqual([1, 3, 4, 5, 9, 10, 11]);
    read.mockImplementation(() => {
      throw new Error("The preparation mapping has been disposed");
    });
    tops.forEach((top, index) => {
      expect(geometry.topForLineNumber(index + 1)).toBe(top);
    });
    for (let offset = -1; offset <= 180; offset++) {
      const expected = Math.max(1, tops.findLastIndex((top) => top <= offset) + 1);
      expect(geometry.lineAtOffset(offset)).toBe(expected);
      expect(reviewLineAtOffset(tops.length, (line) => tops[line - 1]!, offset)).toBe(expected);
    }
  });

  it("does not expand a large hidden range into individual model-line entries", () => {
    const read = vi.fn((line: number) => (line === 100000 ? 200 : 8));
    const geometry = createReviewLineGeometry(100000, [1, 100000], read);
    expect(read.mock.calls.flat()).toEqual([1, 2, 100000]);
    expect(geometry.topForLineNumber(75000)).toBe(8);
    expect(geometry.lineAtOffset(100)).toBe(99999);
    expect(geometry.lineAtOffset(200)).toBe(100000);
  });

  it("retains the authoritative single view position when all model lines are hidden", () => {
    const read = vi.fn(() => 24);
    const geometry = createReviewLineGeometry(30, [], read);
    expect(read).toHaveBeenCalledExactlyOnceWith(1);
    expect(geometry.lineAtOffset(0)).toBe(1);
    expect(geometry.lineAtOffset(24)).toBe(30);
    expect(geometry.topForLineNumber(25)).toBe(24);
  });
});
