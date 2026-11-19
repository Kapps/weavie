import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";

const isAssistantMessage = (entry: AgentTranscriptEntry | undefined): boolean =>
  entry?.kind === "message" && entry.tone === "assistant";

/** The space a transcript row keeps around its entry, from the entry and the one above it. */
export function transcriptRowClasses(
  entry: AgentTranscriptEntry,
  previous: AgentTranscriptEntry | undefined,
): Record<string, boolean> {
  return {
    "agent-row-assistant-pair": isAssistantMessage(entry) && isAssistantMessage(previous),
    "agent-row-first": previous === undefined,
    "agent-row-user": entry.kind === "message" && entry.tone === "user",
  };
}
