import type { AgentTranscriptEntry, AgentTranscriptSnapshot } from "./AgentPaneTranscriptTypes";
import { isAssistantMessage } from "./transcript-row";

/** What the agent has said to one prompt. */
export interface SubmissionReply {
  /** The agent's messages and errors between the prompt and the user's next input. */
  readonly entries: readonly AgentTranscriptEntry[];
  /** Whether the agent may still add to them. */
  readonly working: boolean;
}

const promptIndex = (snapshot: AgentTranscriptSnapshot, id: string): number =>
  snapshot.entries.findLastIndex((entry) => entry.submissionId === id);

/** Whether submission `id`'s prompt is in the transcript. */
export const hasSubmission = (snapshot: AgentTranscriptSnapshot, id: string): boolean =>
  promptIndex(snapshot, id) >= 0;

/** The reply to submission `id`, or null while its prompt is not in the transcript. */
export function submissionReply(
  snapshot: AgentTranscriptSnapshot,
  id: string,
): SubmissionReply | null {
  const all = snapshot.entries;
  let index = promptIndex(snapshot, id);
  if (index < 0) return null;
  // The prompt's own selections and images follow its text; ones that start a turn are a later prompt.
  const attached = (entry: AgentTranscriptEntry | undefined): boolean =>
    entry?.tone === "user" && entry.submissionId === undefined && entry.turnStart !== true;
  do index += 1;
  while (attached(all[index]));
  const entries: AgentTranscriptEntry[] = [];
  for (; index < all.length && all[index]!.tone !== "user"; index += 1) {
    const entry = all[index]!;
    if (isAssistantMessage(entry) || (entry.kind === "notice" && entry.tone === "error"))
      entries.push(entry);
  }
  return { entries, working: index === all.length && snapshot.turnActive };
}
