import { describe, expect, it } from "vitest";
import { agentPlan, setAgentPlan } from "./plan-store";

describe("agent plan store", () => {
  it("keeps one transient tab path per opaque host id while refreshing its document", () => {
    const path = setAgentPlan("session-a:plan-1", "Plan", "# First");
    expect(agentPlan(path)).toEqual({ id: "session-a:plan-1", title: "Plan", markdown: "# First" });

    expect(setAgentPlan("session-a:plan-1", "Updated plan", "# Final")).toBe(path);
    expect(agentPlan(path)).toEqual({
      id: "session-a:plan-1",
      title: "Updated plan",
      markdown: "# Final",
    });
  });

  it("gives distinct host ids distinct virtual paths", () => {
    expect(setAgentPlan("session-a:plan-2", "Plan", "A")).not.toBe(
      setAgentPlan("session-b:plan-2", "Plan", "B"),
    );
  });
});
