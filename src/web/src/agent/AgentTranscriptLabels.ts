import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";

export function assistantSectionLabel(
  entries: AgentTranscriptEntry[],
  index: number,
  turnActive: boolean,
): "Updates" | "Results" | null {
  const entry = entries[index];
  if (entry === undefined || entry.kind !== "message" || entry.tone !== "assistant") {
    return null;
  }
  for (let next = index + 1; next < entries.length; next += 1) {
    const candidate = entries[next];
    if (candidate?.kind === "message") {
      return candidate.tone === "user" ? "Results" : null;
    }
  }
  return turnActive ? "Updates" : "Results";
}
