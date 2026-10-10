import { describe, expect, it } from "vitest";
import { placeHints } from "./hint-anchor";
import { stepHint } from "./hint-order";
import type { ReviewHint } from "./review-hints-store";

const source = "one\ntwo\nthree\nfour\n";
const hint = (id: number, line: number, path = "/w/a.ts"): ReviewHint => ({
  id,
  path,
  line,
  severity: "bug",
  title: "t",
  body: "",
  category: null,
  agent: "Claude",
  sourceHash: "h",
  quote: source.split("\n")[line - 1]!,
});
const sources = new Map([["h", source]]);

describe("placeHints", () => {
  it("keeps a hint on its line when the buffer matches the captured text", () => {
    expect(placeHints([hint(1, 3)], sources, source)[0]).toMatchObject({ line: 3, changed: false });
  });

  it("follows an insertion above the hinted line", () => {
    expect(placeHints([hint(1, 3)], sources, `zero\n${source}`)[0]).toMatchObject({
      line: 4,
      changed: false,
    });
  });

  it("marks a hint changed when its line was edited", () => {
    expect(placeHints([hint(1, 2)], sources, "one\nTWO\nthree\nfour\n")[0]).toMatchObject({
      line: 2,
      changed: true,
    });
  });

  it("keeps a hint whose leading line was deleted on line 1, changed", () => {
    expect(placeHints([hint(1, 1)], sources, "two\nthree\nfour\n")[0]).toMatchObject({
      line: 1,
      changed: true,
    });
  });

  it("refuses a hint without its captured source", () => {
    expect(() => placeHints([hint(1, 1)], new Map(), source)).toThrow();
  });
});

describe("stepHint", () => {
  const hints = [hint(4, 9, "/w/b.ts"), hint(1, 5), hint(2, 2), hint(3, 5)];

  it("walks hints in path, line, id order", () => {
    const order = [];
    let from = { path: "", line: 0, id: 0 };
    for (let i = 0; i < 4; i++) {
      const next = stepHint(hints, from, 1)!;
      order.push(next.id);
      from = next;
    }
    expect(order).toEqual([2, 1, 3, 4]);
  });

  it("wraps around at either end", () => {
    expect(stepHint(hints, hints[0]!, 1)?.id).toBe(2);
    expect(stepHint(hints, hints[2]!, -1)?.id).toBe(4);
  });

  it("steps strictly past the cursor's line", () => {
    expect(stepHint(hints, { path: "/w/a.ts", line: 5, id: Infinity }, 1)?.id).toBe(4);
    expect(stepHint(hints, { path: "/w/a.ts", line: 5, id: -Infinity }, -1)?.id).toBe(2);
  });

  it("finds nothing without hints", () => {
    expect(stepHint([], { path: "/w/a.ts", line: 1, id: 0 }, 1)).toBeUndefined();
  });
});
