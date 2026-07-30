import { describe, expect, it } from "vitest";
import { directSpellWord, ownsSpellProjection } from "./spell-protocol";

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
