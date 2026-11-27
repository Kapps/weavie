import { describe, expect, it } from "vitest";
import { type AgentCardRef, cardForTab, cardTabPath } from "./tab-entry";

describe("card tab paths", () => {
  it("round-trip every card kind that names a block", () => {
    const cards: AgentCardRef[] = [
      { kind: "walkthrough", conversationId: null, blockId: "claims" },
      { kind: "walkthrough", conversationId: "side", blockId: "claims" },
      { kind: "block", conversationId: null, blockId: "p95" },
    ];
    for (const card of cards) {
      const path = cardTabPath(card);
      expect(cardForTab(card.kind, path)).toEqual(card);
    }
    expect(cardTabPath(cards[0]!)).toBe("walkthrough:claims@");
  });
});
