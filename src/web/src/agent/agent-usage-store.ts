import type { AgentUsageSnapshot, ClientSession } from "../bridge";
import { createSessionFeatureValue } from "../messaging/session-feature-value";

const snapshotFor = createSessionFeatureValue<{ state: AgentUsageSnapshot }, AgentUsageSnapshot>(
  "agent",
  "usage",
  ({ state }) => state,
);

/** One exact session's provider-reported usage, or null before any snapshot is available. */
export function agentUsage(session: ClientSession | null): AgentUsageSnapshot | null {
  return snapshotFor(session);
}
