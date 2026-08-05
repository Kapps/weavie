import { afterEach, describe, expect, it, vi } from "vitest";
import {
  BRANCH_PREVIEW_DEBOUNCE_MS,
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

const context = (prompt: string): BranchPreviewContext => ({
  backendId: "local",
  prompt,
  providerId: "codex",
});

afterEach(() => {
  vi.useRealTimers();
});

describe("NewSessionBranchPreview", () => {
  it("debounces a typing burst and requests only the latest prompt", async () => {
    vi.useFakeTimers();
    const requests: BranchPreviewContext[] = [];
    const states: BranchPreviewState[] = [];
    const preview = new NewSessionBranchPreview(
      async (request) => {
        requests.push(request);
        return { branch: "bug/webm-fails-to-load" };
      },
      (state) => states.push(state),
    );

    preview.update(context("WebM"));
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_DEBOUNCE_MS - 1);
    expect(requests).toEqual([]);
    preview.update(context("WebM fails"));
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_DEBOUNCE_MS);

    expect(requests).toEqual([context("WebM fails")]);
    expect(states.at(-1)).toEqual({
      branch: "bug/webm-fails-to-load",
      manual: false,
      status: "ready",
    });
  });

  it("aborts superseded work and ignores a provider that resolves it anyway", async () => {
    vi.useFakeTimers();
    const calls: Array<{ result: Deferred<{ branch: string }>; signal: AbortSignal }> = [];
    let state: BranchPreviewState | undefined;
    const preview = new NewSessionBranchPreview(
      (_request, signal) => {
        const result = deferred<{ branch: string }>();
        calls.push({ result, signal });
        return result.promise;
      },
      (next) => {
        state = next;
      },
    );

    preview.update(context("first"));
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_DEBOUNCE_MS);
    preview.update(context("second"));
    expect(calls[0]!.signal.aborted).toBe(true);
    calls[0]!.result.resolve({ branch: "stale" });
    await Promise.resolve();
    expect(state?.branch).toBe("");

    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_DEBOUNCE_MS);
    calls[1]!.result.resolve({ branch: "fresh" });
    await Promise.resolve();
    expect(state).toEqual({ branch: "fresh", manual: false, status: "ready" });
  });

  it("lets manual input win until the field is explicitly cleared", async () => {
    vi.useFakeTimers();
    const calls: Array<{ context: BranchPreviewContext; signal: AbortSignal }> = [];
    let state: BranchPreviewState | undefined;
    const preview = new NewSessionBranchPreview(
      async (request, signal) => {
        calls.push({ context: request, signal });
        return { branch: "automatic" };
      },
      (next) => {
        state = next;
      },
    );

    preview.update(context("first"));
    preview.edit("mine/fix-webm");
    preview.update({ ...context("second"), providerId: "claude" });
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_DEBOUNCE_MS);
    expect(calls).toEqual([]);
    expect(state).toEqual({ branch: "mine/fix-webm", manual: true, status: "ready" });

    preview.edit("");
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_DEBOUNCE_MS);
    expect(calls).toHaveLength(1);
    expect(calls[0]!.context).toEqual({
      backendId: "local",
      prompt: "second",
      providerId: "claude",
    });
    expect(state).toEqual({ branch: "automatic", manual: false, status: "ready" });
  });

  it("keeps the field editable when the preview transport fails", async () => {
    vi.useFakeTimers();
    let state: BranchPreviewState | undefined;
    const preview = new NewSessionBranchPreview(
      async () => {
        throw new Error("offline");
      },
      (next) => {
        state = next;
      },
    );

    preview.update(context("fix it"));
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_DEBOUNCE_MS);
    expect(state).toEqual({ branch: "", manual: false, status: "error" });

    preview.edit("bug/fix-it");
    expect(state).toEqual({ branch: "bug/fix-it", manual: true, status: "ready" });
  });
});
