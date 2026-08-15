import type { AgentContextWindowUsage } from "../bridge";

const tokens = new Intl.NumberFormat();

export function contextUsedPercent(context: AgentContextWindowUsage): number {
  if (context.capacityTokens <= 0) {
    return 0;
  }
  return Math.round(Math.min(Math.max(context.usedTokens / context.capacityTokens, 0), 1) * 100);
}

export function formatTokenCount(value: number): string {
  return tokens.format(value);
}
