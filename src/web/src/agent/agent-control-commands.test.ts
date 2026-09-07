import { describe, expect, it } from "vitest";
import type { AgentControlAxis } from "../bridge";
import { CommandIds } from "../commands/types";
import { agentControlCommand, agentControlForCommand } from "./agent-control-commands";

function axis(id: string, category: string | null, values: string[]): AgentControlAxis {
  return {
    id,
    category,
    label: id,
    description: null,
    kind: "select",
    value: values[0]!,
    valueLabel: values[0]!,
    options: values.map((value) => ({
      id: value,
      label: value,
      description: null,
      group: null,
    })),
  };
}

describe("plan control command", () => {
  it("finds planning by category despite a preceding permission mode", () => {
    const permission = axis("mode", "mode", ["read-only", "auto", "full-access"]);
    const collaboration = axis("provider-planning", "collaboration_mode", ["default", "plan"]);

    expect(agentControlCommand(permission)).toBeNull();
    expect(agentControlForCommand([permission, collaboration], CommandIds.togglePlanMode)).toBe(
      collaboration,
    );
  });

  it.each([
    "mode",
    "collaboration_mode",
  ])("recognizes an uncategorized %s axis only when planning can be toggled", (id) => {
    expect(agentControlCommand(axis(id, null, ["default", "plan"]))).toBe(
      CommandIds.togglePlanMode,
    );
    expect(agentControlCommand(axis(id, null, ["default"]))).toBeNull();
    expect(agentControlCommand(axis(id, null, ["plan"]))).toBeNull();
  });
});
