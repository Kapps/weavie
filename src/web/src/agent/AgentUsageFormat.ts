import type { AgentContextWindowUsage, AgentUsageLimit } from "../bridge";

const tokens = new Intl.NumberFormat();
const percent = new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 });
const resetTime = new Intl.DateTimeFormat(undefined, {
  month: "short",
  day: "numeric",
  hour: "numeric",
  minute: "2-digit",
});

// The provider names its own windows; anything Weavie has no label for shows the provider's identifier.
const limitLabels: Record<string, string> = {
  five_hour: "5-hour limit",
  seven_day: "Weekly limit",
  seven_day_opus: "Weekly limit (Opus)",
  seven_day_sonnet: "Weekly limit (Sonnet)",
  seven_day_overage_included: "Weekly limit (with overage)",
  overage: "Overage",
};

export function contextUsedPercent(context: AgentContextWindowUsage): number {
  if (context.capacityTokens <= 0) {
    return 0;
  }
  return Math.round(Math.min(Math.max(context.usedTokens / context.capacityTokens, 0), 1) * 100);
}

export function formatTokenCount(value: number): string {
  return tokens.format(value);
}

export function formatLimitLabel(id: string): string {
  return limitLabels[id] ?? id;
}

export function formatLimitValue(limit: AgentUsageLimit): string {
  if (limit.status === "exhausted") {
    return "Limit reached";
  }
  if (limit.usedPercent === null) {
    return limit.status === "warning" ? "Approaching limit" : "Within limits";
  }
  const used = `${percent.format(limit.usedPercent)}% used`;
  return limit.status === "warning" ? `${used} · approaching limit` : used;
}

export function formatResetTime(value: number): string {
  return resetTime.format(new Date(value));
}
