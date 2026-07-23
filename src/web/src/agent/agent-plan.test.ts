import { describe, expect, it } from "vitest";
import type { AgentPaneUpdate } from "../bridge";
import {
  latestCompletedPlan,
  planIdentityArgsSupplied,
  planIdentityFromArgs,
  requestedPlan,
} from "./agent-plan";

const plan = (itemId: string): AgentPaneUpdate => ({
  type: "item-completed",
  providerId: "codex",
  threadId: "thread-1",
  turnId: "turn-1",
  itemId,
  itemType: "plan",
  text: "# Plan",
});

describe("agent plan identity", () => {
  it("selects the newest completed plan and retains its exact opaque identity", () => {
    expect(latestCompletedPlan([plan("first"), plan("latest")])).toEqual({
      threadId: "thread-1",
      turnId: "turn-1",
      itemId: "latest",
    });
  });

  it("refuses an incomplete identity instead of opening a different plan", () => {
    expect(planIdentityFromArgs({ threadId: "thread-1", turnId: "", itemId: "plan-1" })).toBeNull();
    expect(planIdentityArgsSupplied({ threadId: "thread-1", turnId: "", itemId: "plan-1" })).toBe(
      true,
    );
    expect(planIdentityArgsSupplied({})).toBe(true);
    expect(planIdentityArgsSupplied(undefined)).toBe(false);
    expect(planIdentityArgsSupplied(null)).toBe(false);
    expect(
      requestedPlan({ threadId: "thread-1", turnId: "", itemId: "plan-1" }, [plan("usable")]),
    ).toBeNull();
    expect(requestedPlan({}, [plan("usable")])).toBeNull();
    expect(latestCompletedPlan([{ ...plan("plan-1"), turnId: null }])).toBeNull();
    expect(latestCompletedPlan([{ ...plan("plan-2"), text: "  " }])).toBeNull();
    expect(latestCompletedPlan([plan("usable"), { ...plan("blank"), text: " " }])).toEqual({
      threadId: "thread-1",
      turnId: "turn-1",
      itemId: "usable",
    });
  });
});
