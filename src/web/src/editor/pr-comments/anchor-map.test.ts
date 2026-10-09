import { describe, expect, it } from "vitest";
import { createAnchorMap } from "./anchor-map";
import type { PrThread } from "./pr-comments-store";

const thread = (line: number, side: "right" | "left" = "right", outdated = false): PrThread => ({
  rootId: 1,
  path: "/w/a.ts",
  line,
  side,
  outdated,
  comments: [],
});

const head = "one\ntwo\nthree\nfour\n";

describe("createAnchorMap", () => {
  it("keeps a thread on its line when the buffer matches the PR head", () => {
    const map = createAnchorMap({ head, base: head }, head);
    expect(map.place(thread(3))).toMatchObject({ afterLine: 3, note: null, quote: null });
    expect(map.headLine(3)).toBe(3);
  });

  it("follows a local insertion above the commented line", () => {
    const map = createAnchorMap({ head, base: head }, `zero\n${head}`);
    expect(map.place(thread(3))).toMatchObject({ afterLine: 4, note: null });
    expect(map.headLine(4)).toBe(3);
  });

  it("anchors a locally rewritten line after its replacement and quotes the original", () => {
    const map = createAnchorMap({ head, base: head }, "one\nTWO\nthree\nfour\n");
    expect(map.place(thread(2))).toMatchObject({ afterLine: 2, note: "changed", quote: "two" });
  });

  it("refuses a buffer line that exists only locally", () => {
    const map = createAnchorMap({ head, base: head }, "one\nlocal\ntwo\nthree\nfour\n");
    expect(map.headLine(2)).toBeNull();
    expect(map.headLine(3)).toBe(2);
  });

  it("places a left-side comment where its base line was removed", () => {
    const base = "one\nold\ntwo\nthree\nfour\n";
    const map = createAnchorMap({ head, base }, head);
    expect(map.place(thread(2, "left"))).toMatchObject({
      afterLine: 1,
      note: "removed",
      quote: "old",
    });
  });

  it("puts outdated threads at the top of the file", () => {
    const map = createAnchorMap({ head, base: head }, head);
    expect(map.place(thread(0, "right", true))).toMatchObject({ afterLine: 0, note: "outdated" });
  });
});
