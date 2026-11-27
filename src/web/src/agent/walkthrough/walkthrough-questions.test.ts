import { describe, expect, it } from "vitest";
import type { AgentWalkthrough, WalkthroughCode } from "../../bridge";
import type { AgentTranscriptEntry } from "../AgentPaneTranscriptTypes";
import { codeLine, codePlace, placeQuestions } from "./walkthrough-questions";

const code: WalkthroughCode = { path: "a.ts", from: 3, to: 9, source: "aaaaaaaaaaaaaaaaaaaa" };
const walkthrough: AgentWalkthrough = {
  root: "/repo",
  problem: "p",
  gist: "g",
  files: [],
  stops: [{ id: "stage", name: "Stage", title: "Stage", lead: "l", code: [code] }],
};
const asked = (place: string) => ({
  anchor: { conversationId: null, blockId: "w", place, quote: "q", card: "ask" },
  entry: {} as AgentTranscriptEntry,
});

describe("walkthrough questions", () => {
  it("stay where they were asked while that place is drawn", () => {
    const places = ["gist", "stage/lead", codePlace("stage", 0, code, 5)];
    expect(placeQuestions(places.map(asked), walkthrough).map((q) => [q.place, q.moved])).toEqual(
      places.map((place) => [place, false]),
    );
  });

  it("move to their stop's end, or the page's, once their place is gone", () => {
    const recorded = { ...code, source: "bbbbbbbbbbbbbbbbbbbb" };
    const places = [
      codePlace("stage", 0, recorded, 5),
      codePlace("stage", 0, code, 20),
      "stage/why",
      "closing",
      "gone/lead",
    ];
    expect(placeQuestions(places.map(asked), walkthrough).map((q) => [q.place, q.moved])).toEqual([
      ["stage/end", true],
      ["stage/end", true],
      ["stage/end", true],
      ["end", true],
      ["end", true],
    ]);
  });

  it("reads a code place's line back", () => {
    expect(codeLine(codePlace("stage", 0, code, 7), "stage", 0)).toBe(7);
    expect(codeLine(codePlace("stage", 0, code, 7), "stage", 1)).toBeNull();
  });
});
