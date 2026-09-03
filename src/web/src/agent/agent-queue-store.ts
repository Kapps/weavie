// The host-owned set of submissions an agent accepted but has not delivered yet — a provider command
// waiting for the running turn, or any prompt when the provider cannot steer. The host is the only
// writer: it republishes the whole queue on every change, so the composer never infers what is waiting.

import type { AgentQueuedSubmission, ClientSession } from "../bridge";
import { createSessionFeatureValue } from "../messaging/session-feature-value";

const EMPTY: AgentQueuedSubmission[] = [];
const queueFor = createSessionFeatureValue<
  { queued: AgentQueuedSubmission[] },
  AgentQueuedSubmission[]
>("agent", "queue", ({ queued }) => queued);

/** One exact session's waiting submissions, in delivery order. */
export function agentQueuedSubmissions(session: ClientSession | null): AgentQueuedSubmission[] {
  return queueFor(session) ?? EMPTY;
}

/** What a waiting submission reads as: its text, or its image count when it carries only attachments. */
export function queuedSubmissionLabel(submission: AgentQueuedSubmission): string {
  if (submission.text.length > 0) return submission.text;
  return submission.attachments === 1 ? "1 image" : `${submission.attachments} images`;
}
