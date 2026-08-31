import { describe, expect, it } from "vitest";
import { reviewToModelLine } from "../diff-geometry";
import { buildReviewDiffPatch } from "./review-diff-model";

describe("review diff model", () => {
  it("renders replacements with old and new line numbers", () => {
    const patch = buildReviewDiffPatch("one\ntwo\nthree", "one\nTWO\nthree");

    expect(patch.timedOut).toBe(false);
    expect(patch.hunks).toHaveLength(1);
    expect(patch.hunks[0]?.rows).toEqual([
      { kind: "context", oldLine: 1, newLine: 1, text: "one" },
      { kind: "removed", oldLine: 2, newLine: null, text: "two" },
      { kind: "added", oldLine: null, newLine: 2, text: "TWO" },
      { kind: "context", oldLine: 3, newLine: 3, text: "three" },
    ]);
  });

  it("merges nearby changes and separates distant hunks", () => {
    const original = Array.from({ length: 20 }, (_, index) => `line ${index + 1}`).join("\n");
    const modified = original
      .replace("line 3", "changed 3")
      .replace("line 8", "changed 8")
      .replace("line 18", "changed 18");

    const patch = buildReviewDiffPatch(original, modified);

    expect(patch.hunks).toHaveLength(2);
    expect(patch.hunks[0]?.rows.some((row) => row.text === "changed 3")).toBe(true);
    expect(patch.hunks[0]?.rows.some((row) => row.text === "changed 8")).toBe(true);
    expect(patch.hunks[1]?.rows.some((row) => row.text === "changed 18")).toBe(true);
  });

  it("normalizes CRLF and represents additions and deletions", () => {
    const patch = buildReviewDiffPatch("one\r\ntwo\r\nthree", "zero\none\nthree\nfour");
    const rows = patch.hunks.flatMap((hunk) => hunk.rows);

    expect(rows).toContainEqual({ kind: "added", oldLine: null, newLine: 1, text: "zero" });
    expect(rows).toContainEqual({ kind: "removed", oldLine: 2, newLine: null, text: "two" });
    expect(rows).toContainEqual({ kind: "added", oldLine: null, newLine: 4, text: "four" });
  });

  it("builds a sparse patch for a 4,000-line file without a presentation cap", () => {
    const large = Array.from({ length: 4_000 }, (_, index) => `line ${index}`).join("\n");
    const modified = large.replace("line 3000", "changed 3000");
    const patch = buildReviewDiffPatch(large, modified);

    expect(patch.timedOut).toBe(false);
    expect(patch.hunks).toHaveLength(1);
    expect(patch.hunks[0]?.rows.some((row) => row.text === "changed 3000")).toBe(true);
  });

  it("reuses the shared diff geometry to map reviewed anchors", () => {
    const insertion = buildReviewDiffPatch("one\ntwo\nthree", "zero\none\ntwo\nthree");
    const deletion = buildReviewDiffPatch("one\ntwo\nthree", "one\nthree");

    expect(reviewToModelLine(insertion.changes, 2)).toBe(3);
    expect(reviewToModelLine(deletion.changes, 3)).toBe(2);
  });
});
