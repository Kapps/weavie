import { describe, expect, it } from "vitest";
import type { AgentBackgroundItem } from "./agent-background-store";
import { backgroundItemToOpen, backgroundTab } from "./background-tab";

const item = (id: string, kind: AgentBackgroundItem["kind"], startedAtMs: number) =>
  ({ id, kind, startedAtMs }) as AgentBackgroundItem;

describe("background tabs", () => {
  const items = [
    item("task:a", "task", 2),
    item("conv-1", "subagent", 5),
    item("task:b", "task", 3),
  ];

  it("opens the named item, else the most recently started one", () => {
    expect(backgroundItemToOpen(items, "task:b").id).toBe("task:b");
    expect(backgroundItemToOpen(items, undefined).id).toBe("conv-1");
  });

  it("refuses loudly when there is nothing to open", () => {
    expect(() => backgroundItemToOpen(items, "task:gone")).toThrow("no longer listed");
    expect(() => backgroundItemToOpen([], undefined)).toThrow("no background work");
    expect(() => backgroundItemToOpen(items, 7)).toThrow("must be a string");
  });

  it("shows a subagent as its transcript tab and a task as its own tab", () => {
    expect(backgroundTab(items[1]!)).toEqual({ kind: "subagent", path: "subagent:conv-1" });
    expect(backgroundTab(items[0]!)).toEqual({ kind: "task", path: "task:a" });
  });
});
