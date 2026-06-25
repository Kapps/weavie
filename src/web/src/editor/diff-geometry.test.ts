import { describe, expect, it } from "vitest";
import { type LineSpan, reviewToModelLine } from "./diff-geometry";

// A bright hunk = a diff mapping from the review baseline (`original`) to the live model (`modified`).
const hunk = (
  originalStart: number,
  originalEnd: number,
  modifiedStart: number,
  modifiedEnd: number,
): { original: LineSpan; modified: LineSpan } => ({
  original: { startLineNumber: originalStart, endLineNumberExclusive: originalEnd },
  modified: { startLineNumber: modifiedStart, endLineNumberExclusive: modifiedEnd },
});

describe("reviewToModelLine — placing an accepted (faded) hunk in the live model", () => {
  it("is the identity with no bright hunks", () => {
    expect(reviewToModelLine([], 5)).toBe(5);
  });

  it("shifts a line DOWN by the net insertion of a bright hunk above it", () => {
    // Bright hunk inserted 2 lines (original [2,2) empty → modified [2,4)); the faded line at review 5 lands at 7.
    expect(reviewToModelLine([hunk(2, 2, 2, 4)], 5)).toBe(7);
  });

  it("shifts a line UP by the net deletion of a bright hunk above it", () => {
    // Bright hunk removed 2 lines (original [2,4) → modified [2,2) empty); the faded line at review 5 lands at 3.
    expect(reviewToModelLine([hunk(2, 4, 2, 2)], 5)).toBe(3);
  });

  it("ignores a bright hunk at or below the line (only hunks entirely above count)", () => {
    expect(reviewToModelLine([hunk(6, 6, 6, 9)], 5)).toBe(5); // insertion below → no shift
  });

  it("does not count a bright hunk that straddles the line", () => {
    // The line falls inside the hunk's original span — not "entirely above" — so it contributes no delta.
    expect(reviewToModelLine([hunk(4, 7, 4, 9)], 5)).toBe(5);
  });

  it("accumulates the deltas of every bright hunk above the line", () => {
    // +2 (insert at 2) then -1 (replace 2 lines with 1 at original [6,8)); review 10 → 10 + 2 - 1 = 11.
    expect(reviewToModelLine([hunk(2, 2, 2, 4), hunk(6, 8, 8, 9)], 10)).toBe(11);
  });
});
