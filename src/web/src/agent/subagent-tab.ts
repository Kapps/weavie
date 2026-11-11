import type { ClientSession } from "../bridge";
import { subagentConversationId } from "../editor/tab-entry";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { agentPaneModel } from "./pane-store";

/** The live subagent entry a subagent tab shows, from its session's own transcript. */
export function subagentForTab(
  session: ClientSession | null,
  path: string,
): AgentTranscriptEntry | undefined {
  const conversationId = subagentConversationId(path);
  return findSubagent(agentPaneModel(session)?.transcript().entries ?? [], conversationId);
}

function findSubagent(
  entries: readonly AgentTranscriptEntry[],
  conversationId: string,
): AgentTranscriptEntry | undefined {
  for (const entry of entries) {
    if (entry.kind === "subagent" && entry.conversationId === conversationId) return entry;
    const nested = findSubagent(entry.asideEntries ?? [], conversationId);
    if (nested !== undefined) return nested;
  }
  return undefined;
}
