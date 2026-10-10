import type { AgentBackgroundItem } from "./agent-background-store";
import { formatElapsed } from "./turn-progress";

/** "running · 4m 12s · 9 tools · 182k tokens": a background item's state, elapsed time and reported usage. */
export function backgroundStatus(
  state: string,
  startedAtMs: number | null,
  endedAtMs: number | null,
  now: number,
  usage: AgentBackgroundItem["usage"],
): string {
  // A card the host settled after a restart has no end time, so its duration is unknown.
  const live = state === "running" || state === "paused";
  return [
    state,
    startedAtMs === null || (!live && endedAtMs === null)
      ? null
      : formatElapsed((endedAtMs ?? now) - startedAtMs),
    usage?.toolUses ? `${usage.toolUses} tool${usage.toolUses === 1 ? "" : "s"}` : null,
    usage?.totalTokens ? `${compactCount(usage.totalTokens)} tokens` : null,
  ]
    .filter((part) => part !== null)
    .join(" · ");
}

function compactCount(value: number): string {
  return value >= 1000 ? `${Math.round(value / 1000)}k` : String(value);
}

/** Where an arrow, Home, or End key moves focus in a roving toolbar of `count` controls, or null to ignore it. */
export function rovingTarget(key: string, current: number, count: number): number | null {
  if (current < 0 || count === 0) return null;
  switch (key) {
    case "ArrowRight":
      return (current + 1) % count;
    case "ArrowLeft":
      return (current - 1 + count) % count;
    case "Home":
      return 0;
    case "End":
      return count - 1;
    default:
      return null;
  }
}
