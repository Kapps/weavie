import { describe, expect, it, vi } from "vitest";

vi.mock("../monaco-setup", () => ({
  monaco: {
    Range: class {
      constructor(
        readonly startLineNumber: number,
        _startColumn: number,
        readonly endLineNumber: number,
      ) {}
    },
  },
}));
const { collapseUnchanged } = await import("./review-context");
const { FULL_CONTEXT } = await import("./review-store");

import type { DiffMarkers } from "./diff-markers";

// One changed line (50) in a 100-line file: lines 47–53 show, the rest collapses.
const markers = {
  hunks: [{ currentStart: 50, currentEndExclusive: 51 }],
  acceptedHunks: [],
} as unknown as DiffMarkers;
const lines = (revealed: Parameters<typeof collapseUnchanged>[2]) =>
  collapseUnchanged(markers, 100, revealed).map((range) => [
    range.startLineNumber,
    range.endLineNumber,
  ]);

describe("collapseUnchanged", () => {
  it("hides everything beyond the changes' context", () => {
    expect(lines([])).toEqual([
      [1, 46],
      [54, 100],
    ]);
  });

  it("shows revealed spans, merging them with adjacent context", () => {
    expect(lines([{ start: 20, end: 46 }])).toEqual([
      [1, 19],
      [54, 100],
    ]);
  });

  it("shows the whole file for the full-context span", () => {
    expect(lines([FULL_CONTEXT])).toEqual([]);
  });

  it("ignores a revealed span the file has shrunk past", () => {
    expect(lines([{ start: 150, end: 160 }])).toEqual([
      [1, 46],
      [54, 100],
    ]);
  });
});
