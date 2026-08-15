import type { AgentContextWindowUsage, ClientSession } from "../bridge";
import { createSessionFeatureValue } from "../messaging/session-feature-value";

const contextFor = createSessionFeatureValue<
  { state: AgentContextWindowUsage | null },
  AgentContextWindowUsage | null
>("agent", "usage", ({ state }) => state);

/** One exact session's provider-reported context window, or null before any snapshot is available. */
export function agentContextUsage(session: ClientSession | null): AgentContextWindowUsage | null {
  return contextFor(session);
}
