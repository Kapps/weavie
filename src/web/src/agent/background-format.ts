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
  return [
    state,
    backgroundElapsed(state, startedAtMs, endedAtMs, now),
    usage?.toolUses ? `${usage.toolUses} tool${usage.toolUses === 1 ? "" : "s"}` : null,
    usage?.totalTokens ? `${compactCount(usage.totalTokens)} tokens` : null,
  ]
    .filter((part) => part !== null)
    .join(" · ");
}

/** "4m 12s": how long a background item has run or ran, or null when that is unknown. */
export function backgroundElapsed(
  state: string,
  startedAtMs: number | null,
  endedAtMs: number | null,
  now: number,
): string | null {
  // A card the host settled after a restart has no end time, so its duration is unknown.
  const live = state === "running" || state === "paused";
  return startedAtMs === null || (!live && endedAtMs === null)
    ? null
    : formatElapsed((endedAtMs ?? now) - startedAtMs);
}

function compactCount(value: number): string {
  return value >= 1000 ? `${Math.round(value / 1000)}k` : String(value);
}
