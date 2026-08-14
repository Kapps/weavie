import { describe, expect, it } from "vitest";
import { contextUsedPercent } from "./AgentUsageFormat";

describe("contextUsedPercent", () => {
  it.each([
    [{ usedTokens: 40, capacityTokens: 200 }, 20],
    [{ usedTokens: 220, capacityTokens: 200 }, 100],
    [{ usedTokens: -10, capacityTokens: 200 }, 0],
    [{ usedTokens: 10, capacityTokens: 0 }, 0],
  ])("bounds the visible percentage", (context, expected) => {
    expect(contextUsedPercent(context)).toBe(expected);
  });
});
