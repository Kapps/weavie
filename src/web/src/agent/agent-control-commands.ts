import type { AgentControlAxis } from "../bridge";
import { CommandIds } from "../commands/types";

export function agentControlCommand(axis: AgentControlAxis): string | null {
  if (axis.id === "mode" || axis.category === "mode") return CommandIds.togglePlanMode;
  if (axis.id === "fast") return CommandIds.toggleFastMode;
  if (axis.id === "model" || axis.category === "model") return CommandIds.selectModel;
  if (axis.id === "effort" || axis.id === "reasoning" || axis.category === "thought_level") {
    return CommandIds.selectEffort;
  }
  if (axis.id === "approval" || axis.id === "approvalPolicy" || axis.category === "approval") {
    return CommandIds.selectApprovalPolicy;
  }
  if (axis.id === "sandbox" || axis.category === "sandbox") {
    return CommandIds.selectSandbox;
  }
  return null;
}

export function agentControlForCommand(
  axes: readonly AgentControlAxis[],
  commandId: string,
): AgentControlAxis | undefined {
  return axes.find((axis) => agentControlCommand(axis) === commandId);
}
