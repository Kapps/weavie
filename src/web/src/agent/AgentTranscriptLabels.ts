import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";

type SectionLabel = "Updates" | "Results";

/** Computes assistant-result section labels in one back-to-front pass, keyed by entry id.
 * Earlier results are omitted; the final result follows the later user message and active-turn state. */
export function computeSectionLabels(
  entries: readonly AgentTranscriptEntry[],
  turnActive: boolean,
): Map<string, SectionLabel> {
  const labels = new Map<string, SectionLabel>();
  let laterMessageTone: "user" | "assistant" | null = null;
  for (let index = entries.length - 1; index >= 0; index -= 1) {
    const entry = entries[index];
    if (entry === undefined || (entry.kind !== "message" && entry.kind !== "plan")) {
      continue;
    }

    if (isResult(entry)) {
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

/** The first rendered agent entry after the latest prompt boundary, or null when none exists. */
export function latestAgentTurnStartId(entries: readonly AgentTranscriptEntry[]): string | null {
  let turnStart = -1;
  for (let index = entries.length - 1; index >= 0; index -= 1) {
    if (entries[index]?.turnStart === true) {
      turnStart = index;
      break;
    }
  }
  if (turnStart < 0) {
    return null;
  }
  for (let index = turnStart + 1; index < entries.length; index += 1) {
    const entry = entries[index];
    if (entry !== undefined && entry.tone !== "user") {
      return entry.id;
    }
  }
  return null;
}

function isResult(entry: AgentTranscriptEntry): boolean {
  return entry.tone === "assistant" && (entry.kind === "message" || entry.kind === "plan");
}
