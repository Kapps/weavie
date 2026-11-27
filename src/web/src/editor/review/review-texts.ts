import type { ClientSession } from "../../bridge";
import { createRequestCache, type RequestCache } from "../../messaging/request-cache";
import type { ReviewFileDiff } from "./review-store";

/** The baseline texts a file's review renders against its live model. */
export interface ReviewDiffTexts {
  original: string;
  acceptedBaseline: string;
}

/** Fetches one review baseline's text by revision; null once the host has superseded it. */
export type FetchReviewText = (
  session: ClientSession,
  path: string,
  revision: string,
) => Promise<string | null>;

export interface ReviewTexts {
  /** The diff's baseline texts, or null once superseded (a newer diff push follows). */
  resolve(session: ClientSession, diff: ReviewFileDiff): Promise<ReviewDiffTexts | null>;
  /** Drops every cached text the given diffs no longer name. */
  retain(session: ClientSession, diffs: readonly ReviewFileDiff[]): void;
}

/** Content-addressed per-session cache of review baseline texts, sharing in-flight requests. */
export function createReviewTexts(fetch: FetchReviewText): ReviewTexts {
  const sessions = new WeakMap<ClientSession, RequestCache<string | null>>();
  const cacheFor = (session: ClientSession): RequestCache<string | null> => {
    const cache =
      sessions.get(session) ?? createRequestCache<string | null>((text) => text !== null);
    sessions.set(session, cache);
    return cache;
  };

  const request = (session: ClientSession, path: string, revision: string) =>
    cacheFor(session).get(revision, () => fetch(session, path, revision));

  // An absent file reads as empty text. A shared request may predate this diff, so its null proves nothing
  // about this diff: ask once more.
  const text = async (session: ClientSession, path: string, revision: string | null) => {
    if (revision === null) return "";
    const shared = cacheFor(session).has(revision);
    const result = await request(session, path, revision);
    return result === null && shared ? request(session, path, revision) : result;
  };

  return {
    resolve: async (session, diff) => {
      const baseline = text(session, diff.path, diff.baselineRevision);
      const [original, acceptedBaseline] = await Promise.all([
        baseline,
        diff.acceptedBaselineRevision === diff.baselineRevision
          ? baseline
          : text(session, diff.path, diff.acceptedBaselineRevision),
      ]);
      return original === null || acceptedBaseline === null ? null : { original, acceptedBaseline };
    },
    retain: (session, diffs) => {
      const live = new Set(
        diffs.flatMap((diff) => [diff.baselineRevision, diff.acceptedBaselineRevision]),
      );
      cacheFor(session).retain(live);
    },
  };
}
