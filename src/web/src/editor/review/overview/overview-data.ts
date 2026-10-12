import type { ClientSession } from "../../../bridge";
import { createSessionFeatureValue } from "../../../messaging/session-feature-value";
import type { PrUser } from "../../pr-comments/pr-comments-store";

export interface ReviewCommit {
  sha: string;
  author: string;
  timeUnix: number;
  summary: string;
  /** Whether the session's review spans this commit. */
  inReview: boolean;
}

/** The branch's commits since it left its base, newest first. */
export interface ReviewCommits {
  /** The review compares the whole branch with its PR's or the repository's default branch. */
  wholeBranch: boolean;
  commits: ReviewCommit[];
  error: string | null;
}

export type ReviewerState = "pending" | "commented" | "approved" | "changesRequested";
export type CheckState = "pending" | "success" | "failure" | "neutral";

export interface PrConversationItem {
  id: string;
  author: PrUser;
  body: string;
  /** The verdict of a submitted review; null for a plain comment. */
  review: ReviewerState | null;
  createdAt: string;
  url: string;
}

export interface PrDetail {
  number: number;
  url: string;
  /** The branch the PR merges into. */
  baseRef: string;
  /** The branch the PR merges from. */
  headRef: string;
  title: string;
  body: string;
  isDraft: boolean;
  author: PrUser;
  createdAt: string;
  reviewers: { user: PrUser; state: ReviewerState }[];
  conversation: PrConversationItem[];
  checks: { name: string; state: CheckState; url: string }[];
}

export interface PrOverview {
  detail: PrDetail | null;
  error: string | null;
}

export const reviewCommitsFor: (session: ClientSession | null) => ReviewCommits | null =
  createSessionFeatureValue<ReviewCommits, ReviewCommits>("review", "commits", (value) => value);

export const prOverviewFor: (session: ClientSession | null) => PrOverview | null =
  createSessionFeatureValue<PrOverview, PrOverview>("pullRequests", "overview", (value) => value);
