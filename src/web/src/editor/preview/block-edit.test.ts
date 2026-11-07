import { describe, expect, it } from "vitest";
import { gapInsertion, isBareMarker, nextListMarker, relocate, sourceOffset } from "./block-edit";

describe("relocate", () => {
  it("keeps an unmoved block and follows one that moved to its nearest copy", () => {
    expect(relocate(["a", "b", "a"], ["b"], 1)).toBe(1);
    expect(relocate(["x", "x", "a", "b"], ["a", "b"], 0)).toBe(2);
    expect(relocate(["a", "z", "z", "z", "a"], ["a"], 3)).toBe(4);
  });

  it("reports a block whose text is gone", () => {
    expect(relocate(["a", "c"], ["b"], 1)).toBeUndefined();
  });
});

describe("sourceOffset", () => {
  it("maps a rendered caret past the markup rendering removed", () => {
    expect(sourceOffset("some **bold** text", "some bo")).toBe("some **bo".length);
    expect(sourceOffset("## Title", "Ti")).toBe("## Ti".length);
    expect(sourceOffset("- [ ] item", "it")).toBe("- [ ] it".length);
  });
});

describe("list markers", () => {
  it("continues a list with the same bullet, the next number, and an unchecked box", () => {
    expect(nextListMarker("  * nested")).toBe("  * ");
    expect(nextListMarker("9. nine")).toBe("10. ");
    expect(nextListMarker("> - quoted")).toBe("> - ");
    expect(nextListMarker("- [x] done")).toBe("- [ ] ");
  });

  it("recognizes a marker with nothing after it", () => {
    expect(isBareMarker("- [ ] ")).toBe(true);
    expect(isBareMarker("1. ")).toBe(true);
    expect(isBareMarker("- text")).toBe(false);
    expect(isBareMarker("")).toBe(false);
  });
});

describe("gapInsertion", () => {
  const apply = (lines: string[], after: number): string[] => {
    const { at, insert, line } = gapInsertion(lines, after);
    const next = [...lines];
    next.splice(at, 0, ...insert);
    next[line] = "NEW";
    return next;
  };

  it("gives a new block its own line with blank neighbors", () => {
    expect(apply(["a", "", "b"], 1)).toEqual(["a", "", "NEW", "", "b"]);
    expect(apply(["# a", "b"], 1)).toEqual(["# a", "", "NEW", "", "b"]);
    expect(apply(["a"], 1)).toEqual(["a", "", "NEW"]);
    expect(apply(["a", ""], 1)).toEqual(["a", "", "NEW"]);
    expect(apply([""], 0)).toEqual(["NEW"]);
  });
});
