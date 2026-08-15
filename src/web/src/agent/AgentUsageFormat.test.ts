import { describe, expect, it } from "vitest";
import type { AgentUsageLimit } from "../bridge";
import { contextUsedPercent, formatLimitLabel, formatLimitValue } from "./AgentUsageFormat";

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

describe("formatLimitLabel", () => {
  it.each([
    ["five_hour", "5-hour limit"],
    ["seven_day", "Weekly limit"],
    ["seven_day_opus", "Weekly limit (Opus)"],
    // A window Weavie has no label for still names itself rather than disappearing.
    ["fortnightly_experiment", "fortnightly_experiment"],
  ])("names %s", (id, expected) => {
    expect(formatLimitLabel(id)).toBe(expected);
  });
});

describe("formatLimitValue", () => {
  const limit = (patch: Partial<AgentUsageLimit>): AgentUsageLimit => ({
    id: "seven_day",
    status: "allowed",
    usedPercent: null,
    resetsAtMs: null,
    ...patch,
  });

  it.each([
    [limit({ usedPercent: 62 }), "62% used"],
    [limit({ usedPercent: 62, status: "warning" }), "62% used · approaching limit"],
    [limit({ status: "exhausted", usedPercent: 100 }), "Limit reached"],
    // Claude omits utilization until a threshold is crossed, so the row still states what it knows.
    [limit({}), "Within limits"],
    [limit({ status: "warning" }), "Approaching limit"],
  ])("states what the provider reported", (value, expected) => {
    expect(formatLimitValue(value)).toBe(expected);
  });
});
