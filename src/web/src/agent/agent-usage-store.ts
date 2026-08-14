import type { AgentUsageState, ClientSession } from "../bridge";
import { createSessionFeatureValue } from "../messaging/session-feature-value";

const stateFor = createSessionFeatureValue<{ state: AgentUsageState }, AgentUsageState>(
  "agent",
  "usage",
  ({ state }) => state,
);

/** One exact session's provider-reported usage, or null before any snapshot is available. */
export function agentUsageState(session: ClientSession | null): AgentUsageState | null {
  return stateFor(session);
}

/** Whether a session has enough authoritative context data to render a percentage. */
export function hasAgentContextUsage(session: ClientSession | null): boolean {
  const state = agentUsageState(session);
  return state !== null && state.contextWindow !== null;
}
