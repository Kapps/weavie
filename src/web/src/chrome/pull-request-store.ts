import type { ClientSession } from "../bridge";
import { createSessionFeatureValue } from "../messaging/session-feature-value";
import { gitStatusOf } from "./git-status-store";
import type { RailSession } from "./session-store";

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
  return status !== null && status.branch === gitStatusOf(session)?.branch ? status : null;
}

/** Where a session's work stands: its worktree and the pull request for its branch. */
export interface SessionWork {
  dirty: boolean;
  pullRequest: PullRequest | null;
}

/** Where a loaded session's work stands; null until its Git status arrives, outside a branch, and when parked. */
export function sessionWork(session: RailSession): SessionWork | null {
  const git = gitStatusOf(session.owner);
  return git === null || git.branch === null
    ? null
    : {
        dirty: git.dirty,
        pullRequest: currentPullRequestStatus(session.owner)?.pullRequest ?? null,
      };
}
