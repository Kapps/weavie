import { describe, expect, it } from "vitest";
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
