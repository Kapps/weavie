import { afterEach, describe, expect, it, vi } from "vitest";
import type { BranchPreviewResult } from "../bridge";
import {
  BRANCH_PREVIEW_IDLE_MS,
  type BranchPreviewContext,
  type BranchPreviewState,
  NewSessionBranchPreview,
} from "./new-session-branch-preview";

interface Deferred<T> {
  promise: Promise<T>;
  resolve: (value: T) => void;
  reject: (error: Error) => void;
}

const deferred = <T>(): Deferred<T> => {
  let resolve!: (value: T) => void;
  let reject!: (error: Error) => void;
  const promise = new Promise<T>((accept, decline) => {
    resolve = accept;
    reject = decline;
  });
  return { promise, resolve, reject };
};

const DRAFT = "the WebM video in the review pane fails to load whenever the diff is reopened twice";
const GROWN = `${DRAFT} after a reconnect`;
const SHORT = "WebM fails to load";
const VAGUE =
  "something is broken somewhere in the app and it would be good if someone could look at it";
const VAGUE_GROWN = `${VAGUE} in the review pane`;

const context = (prompt: string): BranchPreviewContext => ({
  backendId: "local",
  prompt,
  attachments: [],
});

const imageContext = (dataB64: string): BranchPreviewContext => ({
  ...context(""),
  attachments: [{ id: "image-1", mime: "image/png", dataB64 }],
});

const named = (branch: string): BranchPreviewResult => ({
  branch,
  error: null,
  needsMoreDetail: false,
});

const MORE_DETAIL: BranchPreviewResult = { branch: "", error: null, needsMoreDetail: true };

afterEach(() => {
  vi.useRealTimers();
});

describe("NewSessionBranchPreview", () => {
  it("waits for an idle prompt with enough words to name", async () => {
    vi.useFakeTimers();
    const requests: BranchPreviewContext[] = [];
    const states: BranchPreviewState[] = [];
    const preview = new NewSessionBranchPreview(
      async (request) => {
        requests.push(request);
        return named("bug/webm-fails-to-load");
      },
      (state) => states.push(state),
    );

    preview.update(context(SHORT));
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_IDLE_MS * 2);
    expect(requests).toEqual([]);

    preview.update(context(DRAFT));
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_IDLE_MS - 1);
    expect(requests).toEqual([]);
    await vi.advanceTimersByTimeAsync(1);

    expect(requests).toEqual([context(DRAFT)]);
    expect(states.at(-1)).toEqual({
      branch: "bug/webm-fails-to-load",
      error: null,
      manual: false,
      status: "ready",
    });
  });

  it("settles on the first name and never re-queries as the prompt grows", async () => {
    vi.useFakeTimers();
    const requests: BranchPreviewContext[] = [];
    let state: BranchPreviewState | undefined;
    const preview = new NewSessionBranchPreview(
      async (request) => {
        requests.push(request);
        return named("bug/webm-fails-to-load");
      },
      (next) => {
        state = next;
      },
    );

    preview.update(context(DRAFT));
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_IDLE_MS);
    expect(requests).toHaveLength(1);

    preview.update(context(GROWN));
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_IDLE_MS * 4);
    preview.flush();
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_IDLE_MS);

    expect(requests).toHaveLength(1);
    expect(state?.branch).toBe("bug/webm-fails-to-load");
  });

  it("lets an in-flight query finish instead of restarting it on every keystroke", async () => {
    vi.useFakeTimers();
    const calls: Array<{ result: Deferred<BranchPreviewResult>; signal: AbortSignal }> = [];
    let state: BranchPreviewState | undefined;
    const preview = new NewSessionBranchPreview(
      (_request, _userInitiated, signal) => {
        const result = deferred<BranchPreviewResult>();
        calls.push({ result, signal });
        return result.promise;
      },
      (next) => {
        state = next;
      },
    );

    preview.update(context(DRAFT));
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_IDLE_MS);
    preview.update(context(GROWN));
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_IDLE_MS * 2);

    expect(calls).toHaveLength(1);
    expect(calls[0]!.signal.aborted).toBe(false);
    calls[0]!.result.resolve(named("bug/webm-fails-to-load"));
    await Promise.resolve();
    expect(state?.branch).toBe("bug/webm-fails-to-load");
  });

  it("keeps listening while the model says the prompt names no task yet", async () => {
    vi.useFakeTimers();
    const requests: BranchPreviewContext[] = [];
    let state: BranchPreviewState | undefined;
    const preview = new NewSessionBranchPreview(
      async (request) => {
        requests.push(request);
        return request.prompt.includes("review") ? named("bug/review-pane") : MORE_DETAIL;
      },
      (next) => {
        state = next;
      },
    );

    preview.update(context(VAGUE));
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_IDLE_MS);
    expect(state?.status).toBe("needsDetail");

    // The same prompt is the same question; only new words are worth asking about again.
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_IDLE_MS * 4);
    expect(requests).toHaveLength(1);

    preview.update(context(VAGUE_GROWN));
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_IDLE_MS);
    expect(requests).toHaveLength(2);
    expect(state).toEqual({
      branch: "bug/review-pane",
      error: null,
      manual: false,
      status: "ready",
    });
  });

  it("names the branch immediately when focus leaves a prompt too short to have queried", async () => {
    vi.useFakeTimers();
    const requests: BranchPreviewContext[] = [];
    let state: BranchPreviewState | undefined;
    const preview = new NewSessionBranchPreview(
      async (request) => {
        requests.push(request);
        return named("bug/webm");
      },
      (next) => {
        state = next;
      },
    );

    preview.update(context(SHORT));
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_IDLE_MS * 2);
    expect(requests).toEqual([]);

    preview.flush();
    await Promise.resolve();
    expect(requests).toEqual([context(SHORT)]);
    expect(state?.branch).toBe("bug/webm");
  });

  it("resolves a name for a submission that outran the idle window", async () => {
    vi.useFakeTimers();
    const requests: BranchPreviewContext[] = [];
    const preview = new NewSessionBranchPreview(
      async (request) => {
        requests.push(request);
        return named("bug/webm-fails-to-load");
      },
      () => {},
    );

    preview.update(context(DRAFT));
    expect(await preview.resolve()).toBe("bug/webm-fails-to-load");
    expect(requests).toHaveLength(1);

    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_IDLE_MS * 2);
    expect(requests).toHaveLength(1);
  });

  it("answers a submission for the draft as it now reads, not the query already in flight", async () => {
    vi.useFakeTimers();
    const calls: Array<{ context: BranchPreviewContext; result: Deferred<BranchPreviewResult> }> =
      [];
    let state: BranchPreviewState | undefined;
    const preview = new NewSessionBranchPreview(
      (request) => {
        const result = deferred<BranchPreviewResult>();
        calls.push({ context: request, result });
        return result.promise;
      },
      (next) => {
        state = next;
      },
    );

    preview.update(context(VAGUE));
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_IDLE_MS);
    expect(calls).toHaveLength(1);

    preview.update(context(VAGUE_GROWN));
    const submission = preview.resolve();
    calls[0]!.result.resolve(MORE_DETAIL);
    await vi.advanceTimersByTimeAsync(0);

    expect(calls).toHaveLength(2);
    expect(calls[1]!.context.prompt).toBe(VAGUE_GROWN);
    calls[1]!.result.resolve(named("bug/review-pane"));
    expect(await submission).toBe("bug/review-pane");
    expect(state?.status).toBe("ready");
  });

  it("names the draft again when the host it would be created on changes", async () => {
    vi.useFakeTimers();
    const requests: BranchPreviewContext[] = [];
    let state: BranchPreviewState | undefined;
    const preview = new NewSessionBranchPreview(
      async (request) => {
        requests.push(request);
        return named(`${request.backendId}/webm-fails-to-load`);
      },
      (next) => {
        state = next;
      },
    );

    preview.update(context(DRAFT));
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_IDLE_MS);
    expect(state?.branch).toBe("local/webm-fails-to-load");

    preview.update({ ...context(DRAFT), backendId: "remote-1" });
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_IDLE_MS);

    expect(requests).toHaveLength(2);
    expect(state?.branch).toBe("remote-1/webm-fails-to-load");
  });

  it("explains a submission it still cannot name", async () => {
    vi.useFakeTimers();
    let state: BranchPreviewState | undefined;
    const preview = new NewSessionBranchPreview(
      async () => MORE_DETAIL,
      (next) => {
        state = next;
      },
    );

    preview.update(context(VAGUE));
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_IDLE_MS);

    expect(await preview.resolve()).toBe("");
    expect(state).toEqual({
      branch: "",
      error: "the prompt doesn't describe a specific task yet.",
      manual: false,
      status: "error",
    });
  });

  it("asks as the user only when the user asked, and as the composer otherwise", async () => {
    vi.useFakeTimers();
    const origins: boolean[] = [];
    const preview = new NewSessionBranchPreview(
      async (_request, userInitiated) => {
        origins.push(userInitiated);
        return named("bug/webm-fails-to-load");
      },
      () => {},
    );

    preview.update(context(DRAFT));
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_IDLE_MS);
    expect(origins).toEqual([false]);

    preview.refresh();
    await Promise.resolve();
    expect(origins).toEqual([false, true]);
  });

  it("re-runs a settled suggestion only when asked to", async () => {
    vi.useFakeTimers();
    const branches = ["bug/first-guess", "bug/second-guess"];
    let state: BranchPreviewState | undefined;
    const preview = new NewSessionBranchPreview(
      async () => named(branches.shift() ?? "bug/exhausted"),
      (next) => {
        state = next;
      },
    );

    preview.update(context(DRAFT));
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_IDLE_MS);
    expect(state?.branch).toBe("bug/first-guess");

    preview.update(context(GROWN));
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_IDLE_MS);
    expect(state?.branch).toBe("bug/first-guess");

    preview.refresh();
    await Promise.resolve();
    expect(state).toEqual({
      branch: "bug/second-guess",
      error: null,
      manual: false,
      status: "ready",
    });
  });

  it("replaces a typed name on refresh and abandons the work it supersedes", async () => {
    vi.useFakeTimers();
    const calls: Array<{ result: Deferred<BranchPreviewResult>; signal: AbortSignal }> = [];
    let state: BranchPreviewState | undefined;
    const preview = new NewSessionBranchPreview(
      (_request, _userInitiated, signal) => {
        const result = deferred<BranchPreviewResult>();
        calls.push({ result, signal });
        return result.promise;
      },
      (next) => {
        state = next;
      },
    );

    preview.update({ ...imageContext("shot"), prompt: DRAFT });
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_IDLE_MS);
    preview.edit("mine/typed-name");
    expect(state?.manual).toBe(true);

    preview.refresh();
    expect(calls[0]!.signal.aborted).toBe(true);
    calls[0]!.result.resolve(named("stale"));
    await Promise.resolve();
    expect(state?.status).toBe("loading");

    calls[1]!.result.resolve(named("bug/screenshot"));
    await Promise.resolve();
    expect(state).toEqual({
      branch: "bug/screenshot",
      error: null,
      manual: false,
      status: "ready",
    });
  });

  it("stops naming the field once the user moves into it, and resumes if they leave it empty", async () => {
    vi.useFakeTimers();
    const requests: BranchPreviewContext[] = [];
    let state: BranchPreviewState | undefined;
    const preview = new NewSessionBranchPreview(
      async (request) => {
        requests.push(request);
        return named("bug/automatic-name");
      },
      (next) => {
        state = next;
      },
    );

    preview.update(context(DRAFT));
    preview.claim();
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_IDLE_MS * 3);
    expect(requests).toEqual([]);
    expect(state?.branch).toBe("");

    preview.release();
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_IDLE_MS);
    expect(requests).toHaveLength(1);
    expect(state?.branch).toBe("bug/automatic-name");
  });

  it("lets manual input win until the field is explicitly cleared", async () => {
    vi.useFakeTimers();
    const calls: BranchPreviewContext[] = [];
    let state: BranchPreviewState | undefined;
    const preview = new NewSessionBranchPreview(
      async (request) => {
        calls.push(request);
        return named("automatic");
      },
      (next) => {
        state = next;
      },
    );

    preview.update(context(DRAFT));
    preview.edit("mine/fix-webm");
    preview.update(context(GROWN));
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_IDLE_MS);
    expect(calls).toEqual([]);
    expect(state).toEqual({
      branch: "mine/fix-webm",
      error: null,
      manual: true,
      status: "ready",
    });

    preview.edit("");
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_IDLE_MS);
    expect(calls).toHaveLength(1);
    expect(calls[0]).toEqual({
      backendId: "local",
      prompt: GROWN,
      attachments: [],
    });
    expect(state).toEqual({ branch: "automatic", error: null, manual: false, status: "ready" });
  });

  it("keeps the field editable when the preview transport fails", async () => {
    vi.useFakeTimers();
    let state: BranchPreviewState | undefined;
    const preview = new NewSessionBranchPreview(
      async () => {
        throw new Error("The host is offline.");
      },
      (next) => {
        state = next;
      },
    );

    preview.update(context(DRAFT));
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_IDLE_MS);
    expect(state).toEqual({
      branch: "",
      error: "The host is offline.",
      manual: false,
      status: "error",
    });

    preview.edit("bug/fix-it");
    expect(state).toEqual({
      branch: "bug/fix-it",
      error: null,
      manual: true,
      status: "ready",
    });
  });

  it("preserves the reason when inference fails and stops retrying it", async () => {
    vi.useFakeTimers();
    const requests: BranchPreviewContext[] = [];
    let state: BranchPreviewState | undefined;
    const preview = new NewSessionBranchPreview(
      async (request) => {
        requests.push(request);
        return {
          branch: "",
          error: "ACP authentication was rejected. Run 'acp login' and try again.",
          needsMoreDetail: false,
        };
      },
      (next) => {
        state = next;
      },
    );

    preview.update(context(DRAFT));
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_IDLE_MS);

    expect(state).toEqual({
      branch: "",
      error: "ACP authentication was rejected. Run 'acp login' and try again.",
      manual: false,
      status: "error",
    });

    preview.update(context(GROWN));
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_IDLE_MS * 2);
    expect(requests).toHaveLength(1);
  });
});
