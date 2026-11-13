import { describe, expect, it } from "vitest";
import { placeHints } from "./hint-anchor";
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
