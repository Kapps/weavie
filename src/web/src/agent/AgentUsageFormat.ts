import type { AgentContextWindowUsage, AgentRateLimitUsage } from "../bridge";

const tokens = new Intl.NumberFormat();
const percent = new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 });
const resetTime = new Intl.DateTimeFormat(undefined, {
  month: "short",
  day: "numeric",
  hour: "numeric",
  minute: "2-digit",
});

export function contextUsedPercent(context: AgentContextWindowUsage): number {
  if (context.capacityTokens <= 0) {
    return 0;
  }
  return Math.round(Math.min(Math.max(context.usedTokens / context.capacityTokens, 0), 1) * 100);
}

export function formatTokenCount(value: number): string {
  return tokens.format(value);
}

export function formatUsedPercent(value: number): string {
  return `${percent.format(value)}% used`;
}

export function formatRateLimitLabel(limit: AgentRateLimitUsage): string {
  const window = formatWindow(limit.windowMinutes);
  return limit.label === null ? window : `${limit.label} · ${window}`;
}

export function formatResetTime(value: number): string {
  return resetTime.format(new Date(value));
}

function formatWindow(minutes: number | null): string {
  if (minutes === null) {
    return "Usage limit";
  }
  if (minutes === 60) {
    return "Hourly limit";
  }
  if (minutes === 7 * 24 * 60) {
    return "Weekly limit";
  }
  if (minutes % (24 * 60) === 0) {
    return `${minutes / (24 * 60)}-day limit`;
  }
  if (minutes % 60 === 0) {
    return `${minutes / 60}-hour limit`;
  }
  return `${minutes}-minute limit`;
}
