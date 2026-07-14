import { describe, expect, it } from "vitest";
import type { SearchMatch } from "../bridge";
import {
  groupByFile,
  matchPositions,
  moveSelection,
  type SearchOptions,
  visibleIndices,
} from "./search-model";

const m = (path: string, line: number): SearchMatch => ({ path, line, column: 1, preview: "x" });

const opts = (over: Partial<SearchOptions>): SearchOptions => ({
  caseSensitive: false,
  wholeWord: false,
  regex: false,
  include: "",
  exclude: "",
  ...over,
});

const spans = (positions: Set<number>): number[] => [...positions].sort((a, b) => a - b);

describe("groupByFile", () => {
  it("groups by path in first-seen order, lines in order within", () => {
    const groups = groupByFile([m("a", 1), m("b", 2), m("a", 3)]);
    expect(groups.map((g) => g.path)).toEqual(["a", "b"]);
    expect(groups[0]?.matches.map((x) => x.line)).toEqual([1, 3]);
  });
});

describe("visibleIndices + moveSelection", () => {
  const matches = [m("a", 1), m("a", 2), m("b", 1), m("c", 1)];

  it("skips collapsed groups", () => {
    expect(visibleIndices(matches, new Set(["b"]))).toEqual([0, 1, 3]);
  });

  it("moves within visible rows and clamps at the ends", () => {
    const visible = [0, 1, 3];
    expect(moveSelection(visible, 1, 1)).toBe(3);
    expect(moveSelection(visible, 3, 1)).toBe(3);
    expect(moveSelection(visible, 0, -1)).toBe(0);
  });

  it("lands on the nearest visible row when the selection was hidden by a collapse", () => {
    expect(moveSelection([0, 3], 2, 1)).toBe(3);
    expect(moveSelection([0, 3], 2, -1)).toBe(0);
  });
});

describe("matchPositions (literal)", () => {
  it("highlights every occurrence, case-insensitively by default", () => {
    expect(spans(matchPositions("Foo foo", "foo", opts({})))).toEqual([0, 1, 2, 4, 5, 6]);
  });

  it("respects case sensitivity", () => {
    expect(spans(matchPositions("Foo foo", "foo", opts({ caseSensitive: true })))).toEqual([
      4, 5, 6,
    ]);
  });

  it("whole word only matches at word boundaries", () => {
    expect(spans(matchPositions("cat catalog cat", "cat", opts({ wholeWord: true })))).toEqual([
      0, 1, 2, 12, 13, 14,
    ]);
  });

  it("returns nothing for an empty query", () => {
    expect(matchPositions("anything", "", opts({})).size).toBe(0);
  });
});

describe("matchPositions (regex)", () => {
  it("highlights regex matches", () => {
    expect(spans(matchPositions("ab12cd", "[0-9]+", opts({ regex: true })))).toEqual([2, 3]);
  });

  it("applies whole-word to the whole pattern", () => {
    const positions = matchPositions("go gone", "go|gone", opts({ regex: true, wholeWord: true }));
    expect(spans(positions)).toEqual([0, 1, 3, 4, 5, 6]);
  });

  it("returns nothing (not a crash) for a pattern JS can't parse", () => {
    expect(matchPositions("x", "a{,5}[", opts({ regex: true })).size).toBe(0);
  });

  it("stops on a zero-length match instead of looping", () => {
    expect(matchPositions("abc", "x*", opts({ regex: true })).size).toBe(0);
  });
});
