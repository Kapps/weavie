import type { AgentPaneUpdate } from "../bridge";

export function isAgentPaneDelta(message: AgentPaneUpdate): boolean {
  return (
    message.type === "agent-message-delta" ||
    message.type === "thought-message-delta" ||
    message.type === "user-message-delta" ||
    message.type === "plan-delta" ||
    message.type === "command-output-delta"
  );
}

/** The first content identity survives caption-only deltas and history/live reconciliation. */
export function retainPaneContent<T extends AgentPaneUpdate>(first: AgentPaneUpdate, next: T): T {
  const merged = { ...next };
  for (const key of ["mediaType", "mediaData", "resourceUri"] as const) {
    if (first[key] != null) merged[key] = first[key];
  }
  return merged;
}
