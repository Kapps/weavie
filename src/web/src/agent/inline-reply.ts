import type { AgentTranscriptEntry, AgentTranscriptSnapshot } from "./AgentPaneTranscriptTypes";

/** What the agent has done about one prompt and the follow-ups asked under it. */
export interface SubmissionReply {
  /**
   * The pane's rows that answer the prompt and each follow-up, a follow-up as the user's row: what follows
   * each up to the user's next unrelated input, then the requests they left waiting. Other conversations'
   * cards and the prompts' own attachments are left out.
   */
  readonly entries: readonly AgentTranscriptEntry[];
  /** Whether the agent may still add to them. */
  readonly working: boolean;
}

/** Whether submission `id`'s prompt is in the transcript. */
export const hasSubmission = (snapshot: AgentTranscriptSnapshot, id: string): boolean =>
  snapshot.entries.some((entry) => entry.submissionId === id);

// A prompt's own selections and images follow its text; ones that start a turn are a later prompt.
const attached = (entry: AgentTranscriptEntry): boolean =>
  entry.tone === "user" && entry.submissionId === undefined && entry.turnStart !== true;

/** The reply to submission `id` and its `followUps`, or null while `id`'s prompt is not in the transcript. */
export function submissionReply(
  snapshot: AgentTranscriptSnapshot,
  id: string,
  followUps: readonly string[],
): SubmissionReply | null {
  const all = snapshot.entries;
  const first = all.findLastIndex((entry) => entry.submissionId === id);
  if (first < 0) return null;
  const entries: AgentTranscriptEntry[] = [];
  // The user's rows that are the card's own: its prompts and their attachments.
  const prompts = new Set([all[first]!.id]);
  // Whether the rows being read answer one of the card's prompts, and whether they directly follow it.
  let answering = true;
  let afterPrompt = true;
  const waiting: AgentTranscriptEntry[] = [];
  for (const entry of all.slice(first + 1)) {
    // The pane shows a waiting request last, wherever it was asked; it belongs with the prompt it came after.
    if (entry.promptedBy !== undefined) {
      if (prompts.has(entry.promptedBy)) waiting.push(entry);
      continue;
    }
    if (afterPrompt && attached(entry)) {
      prompts.add(entry.id);
      continue;
    }
    afterPrompt = false;
    if (entry.tone === "user") {
      answering = entry.submissionId !== undefined && followUps.includes(entry.submissionId);
      afterPrompt = answering;
      if (answering) prompts.add(entry.id);
    }
    if (answering && entry.kind !== "aside") entries.push(entry);
  }
  return {
    entries: [...entries, ...waiting],
    working: (answering || waiting.length > 0) && snapshot.turnActive,
  };
}
