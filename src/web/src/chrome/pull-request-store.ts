import type { ClientSession } from "../bridge";
import { createSessionFeatureValue } from "../messaging/session-feature-value";
import { gitStatus } from "./git-status-store";

export interface PullRequestStatus {
  branch: string | null;
  pullRequest: {
    number: number;
    url: string;
    state: "open" | "merged" | "closed";
  } | null;
  error: string | null;
}

export type PullRequest = NonNullable<PullRequestStatus["pullRequest"]>;

export const pullRequestStatus: (session: ClientSession | null) => PullRequestStatus | null =
  createSessionFeatureValue<PullRequestStatus, PullRequestStatus>(
    "git",
    "pullRequest",
    (status) => status,
  );

/** The session's PR status when it describes the checked-out branch; null while detection lags a checkout. */
export function currentPullRequestStatus(session: ClientSession | null): PullRequestStatus | null {
  const status = pullRequestStatus(session);
  return status !== null && status.branch === gitStatus()?.branch ? status : null;
}
