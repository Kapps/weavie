import type { ClientSession } from "../bridge";
import { createSessionFeatureValue } from "../messaging/session-feature-value";

export interface PullRequestStatus {
  branch: string | null;
  pullRequest: {
    number: number;
    url: string;
    state: "open" | "merged" | "closed";
  } | null;
  error: string | null;
}

export const pullRequestStatus: (session: ClientSession | null) => PullRequestStatus | null =
  createSessionFeatureValue<PullRequestStatus, PullRequestStatus>(
    "git",
    "pullRequest",
    (status) => status,
  );
