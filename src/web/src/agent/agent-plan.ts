import type { AgentPaneUpdate } from "../bridge";

export interface AgentPlanIdentity {
  itemId: string;
  threadId: string;
  turnId: string;
}

export function planIdentity(message: AgentPaneUpdate): AgentPlanIdentity | null {
  if (
    !isIdentifier(message.threadId) ||
    !isIdentifier(message.turnId) ||
    !isIdentifier(message.itemId) ||
    typeof message.text !== "string" ||
    message.text.trim().length === 0
  ) {
    return null;
  }
  return {
    itemId: message.itemId,
    threadId: message.threadId,
    turnId: message.turnId,
  };
}

export function planIdentityFromArgs(value: unknown): AgentPlanIdentity | null {
  if (typeof value !== "object" || value === null) {
    return null;
  }
  const args = value as { itemId?: unknown; threadId?: unknown; turnId?: unknown };
  if (!isIdentifier(args.threadId) || !isIdentifier(args.turnId) || !isIdentifier(args.itemId)) {
    return null;
  }
  return {
    itemId: args.itemId,
    threadId: args.threadId,
    turnId: args.turnId,
  };
}

// A bare command opens the newest completed plan; any supplied identity must resolve exactly.
export function planIdentityArgsSupplied(value: unknown): boolean {
  return value !== undefined && value !== null;
}

function isIdentifier(value: unknown): value is string {
  return typeof value === "string" && value.length > 0;
}

export function latestCompletedPlan(
  messages: readonly AgentPaneUpdate[],
): AgentPlanIdentity | null {
  for (let index = messages.length - 1; index >= 0; index -= 1) {
    const message = messages[index];
    if (message?.type === "item-completed" && message.itemType === "plan") {
      const plan = planIdentity(message);
      if (plan !== null) {
        return plan;
      }
    }
  }
  return null;
}

export function requestedPlan(
  args: unknown,
  messages: readonly AgentPaneUpdate[],
): AgentPlanIdentity | null {
  return planIdentityArgsSupplied(args)
    ? planIdentityFromArgs(args)
    : latestCompletedPlan(messages);
}
