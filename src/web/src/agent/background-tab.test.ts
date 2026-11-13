import { describe, expect, it } from "vitest";
import { cardForTab, cardTabPath } from "../editor/tab-entry";
import type { AgentBackgroundItem } from "./agent-background-store";
import { backgroundItemToOpen } from "./background-tab";

const item = (id: string, kind: AgentBackgroundItem["kind"], startedAtMs: number) =>
  ({ id, kind, startedAtMs }) as AgentBackgroundItem;

describe("background tabs", () => {
  const items = [item("task:a", "task", 2), item("conv-1", "subagent", 5)];

  it("opens only the named item, and refuses loudly otherwise", () => {
    expect(backgroundItemToOpen(items, "task:a").id).toBe("task:a");
    expect(() => backgroundItemToOpen(items, undefined)).toThrow("must be a string");
    expect(() => backgroundItemToOpen(items, "task:gone")).toThrow("no longer listed");
  });
});

describe("card tabs", () => {
  it("round-trips every card through its tab path", () => {
    for (const card of [
      { kind: "subagent", conversationId: "c" },
      { kind: "aside", conversationId: "side" },
      { kind: "dashboard", conversationId: null, dashboardId: "rollout" },
      { kind: "dashboard", conversationId: "side", dashboardId: "rollout" },
      { kind: "block", conversationId: null, blockId: "p95" },
    ] as const) {
      expect(cardForTab(card.kind, cardTabPath(card))).toEqual(card);
    }
    expect(cardTabPath({ kind: "dashboard", conversationId: null, dashboardId: "d" })).toBe(
      "dashboard:d@",
    );
    expect(cardForTab("file", "/a")).toBeNull();
  });
});
