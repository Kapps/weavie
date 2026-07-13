import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";

type SectionLabel = "Updates" | "Results";

/**
 * Section label per assistant entry, computed in a single O(entries) back-to-front pass (keyed by entry id) so
 * the transcript never re-derives labels per row. An assistant message is "Results" when a later user message
 * follows it (a finished turn), omitted when a later assistant message follows (it is an earlier update), and —
 * when it is the last message — "Updates" while the turn runs or "Results" once it ends. Omitted ids read as null.
 */
export function computeSectionLabels(
  entries: readonly AgentTranscriptEntry[],
  turnActive: boolean,
): Map<string, SectionLabel> {
  const labels = new Map<string, SectionLabel>();
  let laterMessageTone: "user" | "assistant" | null = null;
  for (let index = entries.length - 1; index >= 0; index -= 1) {
    const entry = entries[index];
    if (entry === undefined || entry.kind !== "message") {
      continue;
    }

    if (entry.tone === "assistant") {
      if (laterMessageTone === "user") {
        labels.set(entry.id, "Results");
      } else if (laterMessageTone === null) {
        labels.set(entry.id, turnActive ? "Updates" : "Results");
      }
    }
    laterMessageTone = entry.tone === "user" ? "user" : "assistant";
  }

  return labels;
}
