import type { AgentTranscriptEntry, AgentTranscriptSnapshot } from "./AgentPaneTranscriptTypes";
import { isAssistantMessage } from "./transcript-row";

/** What the agent has said to one prompt. */
export interface SubmissionReply {
  /** The agent's messages and errors between the prompt and the user's next input. */
  readonly entries: readonly AgentTranscriptEntry[];
  /** Whether the agent may still add to them. */
  readonly working: boolean;
}

/** The reply to submission `id`, or null while its prompt is not in the transcript. */
export function submissionReply(
  snapshot: AgentTranscriptSnapshot,
  id: string,
): SubmissionReply | null {
  const all = snapshot.entries;
  let index = all.findLastIndex((entry) => entry.submissionId === id);
  if (index < 0) return null;
  // The prompt's own selections and images follow its text.
  do index += 1;
  while (all[index]?.tone === "user" && all[index]?.submissionId === undefined);
  const entries: AgentTranscriptEntry[] = [];
  for (; index < all.length && all[index]!.tone !== "user"; index += 1) {
    const entry = all[index]!;
    if (isAssistantMessage(entry) || entry.tone === "error") entries.push(entry);
  }
  return { entries, working: index === all.length && snapshot.turnActive };
}
