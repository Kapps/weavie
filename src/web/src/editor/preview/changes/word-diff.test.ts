import { describe, expect, it } from "vitest";
import { wordDiff } from "./word-diff";

describe("wordDiff", () => {
  it("marks replaced words in the new text and keeps the old ones beside them", () => {
    expect(wordDiff("Send a weekly digest", "Send a daily digest")).toEqual({
      inserted: [{ start: 7, end: 12 }],
      deleted: [{ at: 7, text: "weekly" }],
    });
  });

  it("reports a pure insertion and a pure deletion", () => {
    expect(wordDiff("a c", "a b c")?.inserted).toEqual([{ start: 2, end: 4 }]);
    expect(wordDiff("a b c", "a c")?.deleted).toEqual([{ at: 2, text: "b " }]);
  });
});
