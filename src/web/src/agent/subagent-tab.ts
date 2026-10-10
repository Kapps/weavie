import type { ClientSession } from "../bridge";
import { subagentConversationId } from "../editor/tab-entry";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { agentPaneModel } from "./pane-store";

/** The live subagent entry a subagent tab shows, from its session's own transcript. */
export function subagentEntry(
  session: ClientSession | null,
  path: string,
): AgentTranscriptEntry | undefined {
  const conversationId = subagentConversationId(path);
  return agentPaneModel(session)?.entries.find(
    (entry) => entry.kind === "subagent" && entry.conversationId === conversationId,
  );
}
