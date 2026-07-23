import { describe, expect, it } from "vitest";
import { changedLineNumbers, directSpellWord, ownsSpellProjection } from "./spell-protocol";

describe("changedLineNumbers", () => {
  it("marks only independently changed lines in a multi-cursor batch", () => {
    expect(
      changedLineNumbers(
        [
          { range: { startLineNumber: 9, endLineNumber: 9 }, text: "later" },
          { range: { startLineNumber: 3, endLineNumber: 3 }, text: "earlier" },
        ],
        12,
      ),
    ).toEqual([9, 3]);
  });

  it("marks every resulting line from one pasted multiline edit", () => {
    expect(
      changedLineNumbers(
        [{ range: { startLineNumber: 4, endLineNumber: 4 }, text: "one\ntwo\nthree" }],
        10,
      ),
    ).toEqual([4, 5, 6]);
  });

  it("maps a multiline upper edit before a lower cursor into final document lines", () => {
    expect(
      changedLineNumbers(
        [
          { range: { startLineNumber: 9, endLineNumber: 9 }, text: "later" },
          { range: { startLineNumber: 3, endLineNumber: 3 }, text: "one\ntwo\nthree" },
        ],
        14,
      ),
    ).toEqual([11, 3, 4, 5]);
  });

  it("maps a multiline deletion before a lower multiline cursor into final document lines", () => {
    expect(
      changedLineNumbers(
        [
          { range: { startLineNumber: 9, endLineNumber: 9 }, text: "later\nagain" },
          { range: { startLineNumber: 3, endLineNumber: 5 }, text: "earlier" },
        ],
        11,
      ),
    ).toEqual([7, 8, 3]);
  });
});

describe("ownsSpellProjection", () => {
  const binding = {
    backendId: "remote-a",
    protocol: "projection" as const,
    railSessionId: "rail-a",
    sessionId: "session-a",
    projectionEpoch: "epoch-a",
    projectionRevision: 7,
    projectionPageId: "page-a",
  };

  it("accepts only the exact current projection", () => {
    expect(ownsSpellProjection(binding, binding)).toBe(true);
    expect(ownsSpellProjection(binding, { ...binding, projectionRevision: 8 })).toBe(false);
    expect(ownsSpellProjection(binding, { ...binding, projectionPageId: "page-b" })).toBe(false);
  });

  it("never admits spelling replies to a legacy editor binding", () => {
    expect(
      ownsSpellProjection(
        { backendId: "local", protocol: "legacy", sessionId: "session-a", railSessionId: null },
        binding,
      ),
    ).toBe(false);
  });
});

describe("directSpellWord", () => {
  it("accepts the word supplied by a palette or MCP command", () => {
    expect(directSpellWord({ word: "Weavie" })).toBe("Weavie");
    expect(directSpellWord({ word: "" })).toBeNull();
  });
});
