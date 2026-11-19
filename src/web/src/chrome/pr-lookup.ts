import { type Accessor, createEffect, createSignal, onCleanup } from "solid-js";
import {
  type ClientSession,
  type PullRequestInfo,
  requestPullRequests,
  resolvePullRequest,
} from "../bridge";
import { parsePrRef } from "./pr-ref";

const SEARCH_DEBOUNCE_MS = 250;

/** What Open Pull Request knows for its current query. */
export type PrLookup =
  | { status: "loading" }
  | { status: "listed"; results: PullRequestInfo[] }
  | { status: "resolved"; pr: PullRequestInfo | null }
  | { status: "failed"; message: string };

/**
 * The lookup for `query` in `session`'s repo: a debounced forge-side search, or a #N / URL resolved by number.
 * Every query change cancels the request before it, so only the latest one lands, whichever mode it ran in; a
 * cancelled or closed prompt's request leaves nothing pending.
 */
export function createPrLookup(
  session: ClientSession,
  query: Accessor<string>,
): Accessor<PrLookup> {
  const [lookup, setLookup] = createSignal<PrLookup>({ status: "loading" });
  createEffect(() => {
    const text = query();
    const request = new AbortController();
    setLookup({ status: "loading" });
    const timer = setTimeout(() => {
      const ref = parsePrRef(text);
      const reply: Promise<PrLookup> =
        ref === null
          ? requestPullRequests(session, text.trim(), request.signal).then((results) => ({
              status: "listed",
              results,
            }))
          : resolvePullRequest(session, ref, request.signal).then((pr) => ({
              status: "resolved",
              pr,
            }));
      reply.then(
        (found) => {
          if (!request.signal.aborted) setLookup(found);
        },
        (failure: unknown) => {
          if (!request.signal.aborted)
            setLookup({
              status: "failed",
              message: failure instanceof Error ? failure.message : String(failure),
            });
        },
      );
    }, SEARCH_DEBOUNCE_MS);
    onCleanup(() => {
      request.abort();
      clearTimeout(timer);
    });
  });
  return lookup;
}
