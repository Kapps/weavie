import type { ClientSession } from "../bridge";
import { createSessionFeatureValue } from "../messaging/session-feature-value";

/** One subagent or background task a structured agent runs beside its turns. */
export interface AgentBackgroundItem {
  id: string;
  kind: "subagent" | "task";
  name: string;
  type: string;
  detail: string | null;
  lastActivity: string | null;
  usage: { totalTokens: number | null; toolUses: number | null; durationMs: number | null } | null;
  state: "running" | "paused" | "completed" | "failed" | "stopped";
  canStop: boolean;
  startedAtMs: number;
  endedAtMs: number | null;
  transcriptItemId: string | null;
}

const itemsFor = createSessionFeatureValue<{ items: AgentBackgroundItem[] }, AgentBackgroundItem[]>(
  "agent",
  "background",
  ({ items }) => items,
);

/** One exact session's background work: live items, plus finished ones until its next prompt. */
export function agentBackground(session: ClientSession | null): AgentBackgroundItem[] {
  return itemsFor(session) ?? [];
}

export function backgroundRunning(item: AgentBackgroundItem): boolean {
  return item.state === "running" || item.state === "paused";
}
