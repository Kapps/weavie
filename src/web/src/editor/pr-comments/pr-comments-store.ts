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
  id: number;
  author: string;
  avatarUrl: string;
  /** The comment's forge page; empty when it has none. */
  url: string;
  body: string;
  createdAt: string;
  updatedAt: string;
  mine: boolean;
}

export interface PrThread {
  rootId: number;
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
  changedPaths: string[];
  threads: PrThread[];
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

/** Posts one comment mutation; resolves to the host's error, or null once it landed. */
export async function sendPrComment(
  session: ClientSession,
  kind: "comment" | "reply" | "editComment",
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
