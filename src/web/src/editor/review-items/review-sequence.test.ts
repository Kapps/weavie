import { describe, expect, it } from "vitest";
import type { PrThread } from "../pr-comments/pr-comments-store";
import type { ReviewHint } from "../review-hints/review-hints-store";
import { buildSequence, type ItemFilter, stepItem } from "./review-sequence";

const thread = (
  id: string,
  path: string,
  line: number,
  extra: Partial<PrThread> = {},
): PrThread => ({
  id,
  path,
  line,
  side: "right",
  outdated: false,
  resolved: false,
  comments: [],
  ...extra,
});
const hint = (id: number, path: string, line: number): ReviewHint => ({
  id,
  path,
  line,
  severity: "bug",
  title: "t",
  body: "",
  category: null,
  agent: "Claude",
  sourceHash: "h",
  quote: "",
});
const all: ItemFilter = { comments: true, hints: true, settled: false };
const review = ["/w/z.ts", "/w/a.ts"];
const keys = (items: { key: string }[]) => items.map((item) => item.key);

describe("buildSequence", () => {
  const threads = [
    thread("t1", "/w/a.ts", 5),
    thread("t2", "/w/z.ts", 9),
    thread("t3", "/w/b.ts", 1),
  ];
  const hints = [hint(1, "/w/a.ts", 5), hint(2, "/w/a.ts", 2), hint(3, "/w/c.ts", 1)];

  it("follows the review's file order, then other files by path, then line, threads first", () => {
    expect(keys(buildSequence(threads, hints, review, all))).toEqual([
      "thread:t2",
      "hint:2",
      "thread:t1",
      "hint:1",
      "thread:t3",
      "hint:3",
    ]);
  });

  it("drops a hidden kind", () => {
    expect(keys(buildSequence(threads, hints, review, { ...all, comments: false }))).toEqual([
      "hint:2",
      "hint:1",
      "hint:3",
    ]);
    expect(keys(buildSequence(threads, hints, review, { ...all, hints: false }))).toEqual([
      "thread:t2",
      "thread:t1",
      "thread:t3",
    ]);
  });

  it("skips resolved and outdated threads unless asked for them", () => {
    const settled = [
      thread("open", "/w/a.ts", 3),
      thread("done", "/w/a.ts", 4, { resolved: true }),
      thread("old", "/w/a.ts", 0, { outdated: true }),
    ];
    expect(keys(buildSequence(settled, [], review, all))).toEqual(["thread:open"]);
    expect(keys(buildSequence(settled, [], review, { ...all, settled: true }))).toEqual([
      "thread:old",
      "thread:open",
      "thread:done",
    ]);
  });
});

describe("stepItem", () => {
  const sequence = buildSequence(
    [thread("t1", "/w/a.ts", 5)],
    [hint(1, "/w/z.ts", 3), hint(2, "/w/a.ts", 9)],
    review,
    all,
  );

  it("steps to the neighbour of an item and wraps at either end", () => {
    expect(stepItem(sequence, review, "hint:1", 1)?.key).toBe("thread:t1");
    expect(stepItem(sequence, review, "hint:2", 1)?.key).toBe("hint:1");
    expect(stepItem(sequence, review, "hint:1", -1)?.key).toBe("hint:2");
  });

  it("steps from a location to the nearest item past it, in diff order", () => {
    expect(stepItem(sequence, review, { path: "/w/a.ts", line: 6 }, 1)?.key).toBe("hint:2");
    expect(stepItem(sequence, review, { path: "/w/a.ts", line: 5 }, 1)?.key).toBe("hint:2");
    expect(stepItem(sequence, review, { path: "/w/a.ts", line: 6 }, -1)?.key).toBe("thread:t1");
    expect(stepItem(sequence, review, { path: "/w/z.ts", line: 50 }, 1)?.key).toBe("thread:t1");
    expect(stepItem(sequence, review, { path: "/w/q.ts", line: 1 }, 1)?.key).toBe("hint:1");
  });

  it("starts at the first or last item without an origin, or from an item no longer listed", () => {
    expect(stepItem(sequence, review, null, 1)?.key).toBe("hint:1");
    expect(stepItem(sequence, review, null, -1)?.key).toBe("hint:2");
    expect(stepItem(sequence, review, "thread:gone", 1)?.key).toBe("hint:1");
  });

  it("finds nothing in an empty sequence", () => {
    expect(stepItem([], review, null, 1)).toBeUndefined();
    expect(stepItem([], review, { path: "/w/a.ts", line: 1 }, 1)).toBeUndefined();
  });
});
