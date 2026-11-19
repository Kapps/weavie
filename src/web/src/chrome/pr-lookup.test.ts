import { createRoot, createSignal } from "solid-js";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import type { ClientSession, PullRequestInfo } from "../bridge";

vi.mock("solid-js", () => import(["solid-js", "dist/solid.js"].join("/")));
// Each lookup's reply is held until the test settles it, in whatever order it chooses.
const replies = vi.hoisted(() => ({
  pending: [] as {
    kind: string;
    signal: AbortSignal;
    settle: (outcome: unknown, failed: boolean) => void;
  }[],
}));
vi.mock("../bridge", () => {
  const held =
    (kind: string) =>
    (_session: unknown, _query: unknown, signal: AbortSignal): Promise<unknown> =>
      new Promise((resolve, reject) =>
        replies.pending.push({
          kind,
          signal,
          settle: (outcome, failed) => (failed ? reject(outcome) : resolve(outcome)),
        }),
      );
  return { requestPullRequests: held("list"), resolvePullRequest: held("resolve") };
});

const { createPrLookup } = await import("./pr-lookup");

const pr = { number: 7, title: "Seven" } as PullRequestInfo;
const session = {} as ClientSession;

function lookupOf(first: string) {
  const [query, setQuery] = createSignal(first);
  return createRoot((dispose) => ({ lookup: createPrLookup(session, query), setQuery, dispose }));
}

async function settled(): Promise<void> {
  await vi.runAllTimersAsync();
}

beforeEach(() => {
  vi.useFakeTimers();
  replies.pending.length = 0;
});
afterEach(() => {
  vi.useRealTimers();
});

it("keeps a resolved #N when the search before it fails afterwards", async () => {
  const { lookup, setQuery } = lookupOf("");
  await settled();
  setQuery("#7");
  await settled();
  const [list, resolve] = replies.pending;
  expect([list?.kind, resolve?.kind]).toEqual(["list", "resolve"]);

  resolve?.settle(pr, false);
  await settled();
  list?.settle(new Error("GitHub search failed."), true);
  await settled();
  expect(lookup()).toEqual({ status: "resolved", pr });
});

it("keeps a search's empty result when the #N before it fails afterwards", async () => {
  const { lookup, setQuery } = lookupOf("#7");
  await settled();
  setQuery("nothing");
  await settled();
  const [resolve, list] = replies.pending;
  expect([resolve?.kind, list?.kind]).toEqual(["resolve", "list"]);

  list?.settle([], false);
  await settled();
  resolve?.settle(new Error("GitHub lookup failed."), true);
  await settled();
  expect(lookup()).toEqual({ status: "listed", results: [] });
});

it("reports the current query's failure and lists nothing from before it", async () => {
  const { lookup, setQuery } = lookupOf("");
  await settled();
  replies.pending[0]?.settle([pr], false);
  await settled();
  expect(lookup()).toEqual({ status: "listed", results: [pr] });

  setQuery("seven");
  expect(lookup()).toEqual({ status: "loading" });
  await settled();
  replies.pending[1]?.settle(new Error("GitHub search failed."), true);
  await settled();
  expect(lookup()).toEqual({ status: "failed", message: "GitHub search failed." });
});

it("cancels its request on each new query and when the prompt closes", async () => {
  const { setQuery, dispose } = lookupOf("");
  await settled();
  setQuery("#7");
  await settled();
  const [list, resolve] = replies.pending;
  expect(list?.signal.aborted).toBe(true);
  expect(resolve?.signal.aborted).toBe(false);

  dispose();
  expect(resolve?.signal.aborted).toBe(true);
});
