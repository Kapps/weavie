import { describe, expect, it, vi } from "vitest";
import type { ClientSession } from "../../bridge";
import type { ReviewFileDiff } from "./review-store";
import { createReviewTexts } from "./review-texts";

const session = (): ClientSession => ({}) as ClientSession;

const diff = (baselineRevision: string, acceptedBaselineRevision: string): ReviewFileDiff => ({
  rejected: [],
  path: "/work/a.ts",
  name: "a.ts",
  acceptedBaselineRevision,
  acceptedBaselineExists: true,
  baselineRevision,
  baselineExists: true,
  revision: "current",
  currentExists: true,
});

function deferredFetch() {
  const calls: Array<{ revision: string; resolve: (text: string | null) => void }> = [];
  const fetch = vi.fn(
    (_session: ClientSession, _path: string, revision: string) =>
      new Promise<string | null>((resolve) => calls.push({ revision, resolve })),
  );
  const answer = (revision: string, text: string | null): void => {
    const matching = calls.filter((entry) => entry.revision === revision);
    calls.splice(0, calls.length, ...calls.filter((entry) => entry.revision !== revision));
    for (const call of matching) call.resolve(text);
  };
  return { fetch, calls, answer };
}

describe("review texts", () => {
  it("shares one in-flight request per revision and one request when both baselines match", async () => {
    const { fetch, answer } = deferredFetch();
    const texts = createReviewTexts(fetch);
    const client = session();

    const first = texts.resolve(client, diff("b", "b"));
    const second = texts.resolve(client, diff("b", "b"));
    expect(fetch).toHaveBeenCalledTimes(1);
    answer("b", "base");

    await expect(first).resolves.toEqual({ original: "base", acceptedBaseline: "base" });
    await expect(second).resolves.toEqual({ original: "base", acceptedBaseline: "base" });
    await texts.resolve(client, diff("b", "b"));
    expect(fetch).toHaveBeenCalledTimes(1);
  });

  it("prunes revisions no retained diff names", async () => {
    const { fetch, answer } = deferredFetch();
    const texts = createReviewTexts(fetch);
    const client = session();
    const kept = diff("b", "a");
    const resolved = texts.resolve(client, kept);
    answer("b", "base");
    answer("a", "anchor");
    await resolved;

    texts.retain(client, [diff("b", "b")]);
    const again = texts.resolve(client, kept);
    expect(fetch.mock.calls.map(([, , revision]) => revision)).toEqual(["b", "a", "a"]);
    answer("a", "anchor");
    await expect(again).resolves.toEqual({ original: "base", acceptedBaseline: "anchor" });
  });

  it("resolves a superseded revision to null without caching it", async () => {
    const { fetch, answer } = deferredFetch();
    const texts = createReviewTexts(fetch);
    const client = session();

    const superseded = texts.resolve(client, diff("b", "b"));
    answer("b", null);
    await expect(superseded).resolves.toBeNull();

    const fresh = texts.resolve(client, diff("b", "b"));
    expect(fetch).toHaveBeenCalledTimes(2);
    answer("b", "base");
    await expect(fresh).resolves.toEqual({ original: "base", acceptedBaseline: "base" });
  });

  it("asks again when a request shared from before this diff comes back superseded", async () => {
    const { fetch, calls, answer } = deferredFetch();
    const texts = createReviewTexts(fetch);
    const client = session();

    const earlier = texts.resolve(client, diff("b", "b"));
    const later = texts.resolve(client, diff("b", "b"));
    answer("b", null);
    await expect(earlier).resolves.toBeNull();
    await vi.waitFor(() => expect(calls).toHaveLength(1));
    answer("b", "base");

    await expect(later).resolves.toEqual({ original: "base", acceptedBaseline: "base" });
    expect(fetch).toHaveBeenCalledTimes(2);
  });

  it("keeps sessions apart", async () => {
    const { fetch, answer } = deferredFetch();
    const texts = createReviewTexts(fetch);
    void texts.resolve(session(), diff("b", "b"));
    void texts.resolve(session(), diff("b", "b"));
    answer("b", "base");
    expect(fetch).toHaveBeenCalledTimes(2);
  });
});
