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
  providerId: "acp",
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

  it("finds a pending plan review, including one that names a plan file", () => {
    const review = {
      type: "approval-requested",
      providerId: "fake",
      threadId: "thread",
      turnId: "turn",
      itemId: "request:7",
      requestId: "7",
      itemType: "plan-review",
      planPath: "/plans/claude.md",
    };
    expect(latestCompletedPlan([plan("first"), review])).toEqual({
      itemId: "request:7",
      threadId: "thread",
      turnId: "turn",
    });
  });
});
