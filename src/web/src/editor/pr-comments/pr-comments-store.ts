import { createSignal } from "solid-js";
import type { ClientSession } from "../../bridge";
import type { CommandResult } from "../../commands/types";
import { describeError } from "../../lsp/lsp-errors";
import { createSessionFeatureValue } from "../../messaging/session-feature-value";

export interface PrUser {
  login: string;
  /** Empty when the forge has no avatar for this user. */
  avatarUrl: string;
}

export interface PrComment {
  id: string;
  author: string;
  authorAvatarUrl: string;
  /** The comment's forge page; empty when it has none. */
  url: string;
  body: string;
  createdAt: string;
  updatedAt: string;
  /** A draft in the viewer's pending review, visible only to them until they submit it. */
  pending: boolean;
  mine: boolean;
}

export interface PrThread {
  id: string;
  path: string;
  line: number;
  side: "right" | "left";
  outdated: boolean;
  comments: PrComment[];
}

/** The review comments of the pull request this session's branch belongs to. */
export interface PrCommentSet {
  number: number;
  url: string;
  headSha: string;
  viewer: PrUser;
  /** The viewer opened the PR, so their review can only comment. */
  viewerIsAuthor: boolean;
  /** The viewer's unsubmitted review; new comments and replies join it while it exists. */
  pending: PrPendingReview | null;
  changedPaths: string[];
  threads: PrThread[];
}

export interface PrPendingReview {
  id: string;
  /** The summary drafted so far (a review started on the forge's site may carry one). */
  body: string;
}

/** Whether a new comment posts now or joins the viewer's pending review. */
export type PrCommentMode = "now" | "review";

export type PrVerdict = "comment" | "approve" | "requestChanges";

/** Every draft in the viewer's pending review, with the thread it sits in. */
export function pendingDrafts(set: PrCommentSet): { thread: PrThread; comment: PrComment }[] {
  return set.threads.flatMap((thread) =>
    thread.comments.filter((comment) => comment.pending).map((comment) => ({ thread, comment })),
  );
}

export interface PrCommentsState {
  set: PrCommentSet | null;
  error: string | null;
}

/** A file's text at the PR head and at its merge-base; null where the file doesn't exist. */
export interface PrSources {
  head: string | null;
  base: string | null;
}

export const prCommentsFor: (session: ClientSession | null) => PrCommentsState | null =
  createSessionFeatureValue<PrCommentsState, PrCommentsState>(
    "pullRequests",
    "comments",
    (state) => state,
  );

const [visible, setVisible] = createSignal(true);

/** Whether inline PR threads are shown; one switch for every editor. */
export const prCommentsVisible = visible;

export function togglePrComments(): void {
  setVisible((shown) => !shown);
}

export function showPrComments(): void {
  setVisible(true);
}

/** Sends one comment or review write; resolves to the host's error, or null once it landed. */
export async function sendPrComment(
  session: ClientSession,
  kind: "comment" | "reply" | "editComment" | "discardComment" | "submitReview" | "discardReview",
  payload: object,
): Promise<string | null> {
  try {
    const result = await session
      .feature("pullRequests")
      .request<CommandResult, object>(kind, payload);
    return result.ok ? null : (result.error ?? "The comment couldn't be saved.");
  } catch (error) {
    return describeError(error);
  }
}

/** A new comment's or reply's sends: queue into the pending review first; post now only while none exists. */
export function queueActions(
  set: PrCommentSet,
  send: (mode: PrCommentMode, body: string) => Promise<string | null>,
): {
  submitLabel: string;
  onSubmit: (body: string) => Promise<string | null>;
  alternate: { label: string; onSubmit: (body: string) => Promise<string | null> } | null;
  note: string;
} {
  const drafts = pendingDrafts(set).length;
  return {
    submitLabel: set.pending === null ? "Start review" : "Add to review",
    onSubmit: (body) => send("review", body),
    alternate:
      set.pending === null ? { label: "Comment now", onSubmit: (body) => send("now", body) } : null,
    note:
      set.pending === null ? "" : `Review pending · ${drafts} comment${drafts === 1 ? "" : "s"}`,
  };
}
