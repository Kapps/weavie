import { describe, expect, it } from "vitest";
import { computeTextDiffLines } from "../../review/diff-computation";
import type { DiffHunk } from "../../review/diff-markers";
import { markdownUnits, splitLines } from "../markdown-blocks";
import { createMarkdownEngine } from "../markdown-renderer";
import { type BlockChange, blockChanges, mostlyRewritten } from "./block-changes";

const { markdown } = createMarkdownEngine({
  html: "any",
  allowImages: true,
  allowMermaid: true,
  safeLinksOnly: false,
});

// Line hunks the way the inline diff reports them: 1-based, end-exclusive, from Monaco's line diff.
function lineHunks(before: string, after: string): DiffHunk[] {
  return (computeTextDiffLines(before, after) ?? []).map(({ original, modified }) => ({
    anchorLine: modified.startLineNumber,
    baselineStart: original.startLineNumber,
    baselineEndExclusive: original.endLineNumberExclusive,
    currentStart: modified.startLineNumber,
    currentEndExclusive: modified.endLineNumberExclusive,
  }));
}

function changes(before: string, after: string): BlockChange[] {
  const side = (text: string) => ({
    units: markdownUnits(markdown, text),
    lines: splitLines(text),
  });
  return blockChanges(lineHunks(before, after), side(after), side(before));
}

// Applying every change's current lines over its baseline lines must rebuild `after` exactly.
function applyAll(before: string, after: string, found: BlockChange[]): string {
  const was = splitLines(before);
  const now = splitLines(after);
  for (const { hunk } of [...found].reverse())
    was.splice(
      hunk.baselineStart - 1,
      hunk.baselineEndExclusive - hunk.baselineStart,
      ...now.slice(hunk.currentStart - 1, hunk.currentEndExclusive - 1),
    );
  return was.join("\n");
}

describe("blockChanges", () => {
  it("widens a one-line edit to its whole paragraph", () => {
    const before = "# T\n\nOne\ntwo\nthree\n\nAfter\n";
    const after = "# T\n\nOne\nTWO\nthree\n\nAfter\n";
    const [change] = changes(before, after);
    expect(change?.hunk).toMatchObject({ currentStart: 3, currentEndExclusive: 6 });
    expect(change?.modified).toEqual([
      { current: { start: 2, end: 5 }, baseline: { start: 2, end: 5 } },
    ]);
  });

  it("keeps edits in separate list items as separate changes", () => {
    const found = changes("- a\n- b\n- c\n", "- A\n- b\n- C\n");
    expect(found.map((c) => c.modified.map((m) => m.current.start))).toEqual([[0], [2]]);
  });

  it("reports a removed block before the block that now follows it", () => {
    const [change] = changes("One\n\nGone\n\nThree\n", "One\n\nThree\n");
    expect(change?.removed).toEqual([{ start: 2, end: 3 }]);
    expect(change?.added).toEqual([]);
    expect(change?.at).toBe(2);
  });

  it("reports a new block as added", () => {
    const [change] = changes("One\n", "One\n\n## New\n");
    expect(change?.added).toEqual([{ start: 2, end: 3 }]);
  });

  it("folds a blank-line-only change into the next block without marking it", () => {
    const [change] = changes("One\n\nTwo\n", "One\n\n\nTwo\n");
    expect(change?.modified).toEqual([]);
    expect(change?.added).toEqual([]);
  });

  it("covers every line hunk, so keeping all of them keeps the file", () => {
    const cases = [
      ["# T\n\nA\n\n- x\n- y\n\nB\n", "# T!\n\nA\nmore\n\n- x\n- why\n- z\n\n"],
      ["one\n\ntwo\n\nthree\n", "zero\n\none\n\nthree\n\nfour\n"],
      ["```\ncode\n```\n\ntext\n", "```\ncode2\n```\ntext\n"],
    ] as const;
    for (const [before, after] of cases)
      expect(applyAll(before, after, changes(before, after))).toBe(after);
  });
});

describe("mostlyRewritten", () => {
  it("is true for a new file or when more than half the units changed", () => {
    expect(mostlyRewritten([], 4, true)).toBe(true);
    expect(mostlyRewritten(changes("a\n\nb\n\nc\n", "A\n\nB\n\nc\n"), 3, false)).toBe(true);
    expect(mostlyRewritten(changes("a\n\nb\n\nc\n", "A\n\nb\n\nc\n"), 3, false)).toBe(false);
  });
});
