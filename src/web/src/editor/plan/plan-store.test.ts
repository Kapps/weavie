import { describe, expect, it } from "vitest";
import type { ClientSession } from "../../messaging/host-connection";
import { agentPlan, setAgentPlan } from "./plan-store";

describe("agent plan store", () => {
  it("keeps one transient tab path per opaque host id while refreshing its document", () => {
    const session = {} as ClientSession;
    const path = setAgentPlan(session, "agent-plan:1", "plan-1", "Plan", "# First");
    expect(agentPlan(session, path)).toEqual({ id: "plan-1", title: "Plan", markdown: "# First" });

    expect(setAgentPlan(session, "agent-plan:1", "plan-1", "Updated plan", "# Final")).toBe(path);
    expect(agentPlan(session, path)).toEqual({
      id: "plan-1",
      title: "Updated plan",
      markdown: "# Final",
    });
  });

  it("keeps equal host ids isolated by their owning session", () => {
    const first = {} as ClientSession;
    const second = {} as ClientSession;
    const firstPath = setAgentPlan(first, "agent-plan:1", "plan-1", "First", "A");
    const secondPath = setAgentPlan(second, "agent-plan:1", "plan-1", "Second", "B");

    expect(firstPath).toBe(secondPath);
    expect(agentPlan(first, firstPath)?.title).toBe("First");
    expect(agentPlan(second, secondPath)?.title).toBe("Second");
  });
});
