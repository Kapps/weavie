import { afterEach, describe, expect, it, vi } from "vitest";
import type { BranchPreviewResult } from "../bridge";
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
  attachments: [],
  providerId: "acp",
});

const imageContext = (dataB64: string): BranchPreviewContext => ({
  ...context(""),
  attachments: [{ id: "image-1", mime: "image/png", dataB64 }],
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
        return { branch: "bug/webm-fails-to-load", error: null };
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
      error: null,
      manual: false,
      status: "ready",
    });
  });

  it("aborts superseded work and ignores a provider that resolves it anyway", async () => {
    vi.useFakeTimers();
    const calls: Array<{ result: Deferred<BranchPreviewResult>; signal: AbortSignal }> = [];
    let state: BranchPreviewState | undefined;
    const preview = new NewSessionBranchPreview(
      (_request, signal) => {
        const result = deferred<BranchPreviewResult>();
        calls.push({ result, signal });
        return result.promise;
      },
      (next) => {
        state = next;
      },
    );

    preview.update(imageContext("first"));
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_DEBOUNCE_MS);
    preview.update(imageContext("second"));
    expect(calls[0]!.signal.aborted).toBe(true);
    calls[0]!.result.resolve({ branch: "stale", error: null });
    await Promise.resolve();
    expect(state?.branch).toBe("");

    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_DEBOUNCE_MS);
    calls[1]!.result.resolve({ branch: "fresh", error: null });
    await Promise.resolve();
    expect(state).toEqual({ branch: "fresh", error: null, manual: false, status: "ready" });
  });

  it("lets manual input win until the field is explicitly cleared", async () => {
    vi.useFakeTimers();
    const calls: Array<{ context: BranchPreviewContext; signal: AbortSignal }> = [];
    let state: BranchPreviewState | undefined;
    const preview = new NewSessionBranchPreview(
      async (request, signal) => {
        calls.push({ context: request, signal });
        return { branch: "automatic", error: null };
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
    expect(state).toEqual({
      branch: "mine/fix-webm",
      error: null,
      manual: true,
      status: "ready",
    });

    preview.edit("");
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_DEBOUNCE_MS);
    expect(calls).toHaveLength(1);
    expect(calls[0]!.context).toEqual({
      backendId: "local",
      prompt: "second",
      attachments: [],
      providerId: "claude",
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

    preview.update(context("fix it"));
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_DEBOUNCE_MS);
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

  it("preserves the reason when inference fails", async () => {
    vi.useFakeTimers();
    let state: BranchPreviewState | undefined;
    const preview = new NewSessionBranchPreview(
      async () => ({
        branch: "",
        error: "ACP authentication was rejected. Run 'acp login' and try again.",
      }),
      (next) => {
        state = next;
      },
    );

    preview.update(context("fix it"));
    await vi.advanceTimersByTimeAsync(BRANCH_PREVIEW_DEBOUNCE_MS);

    expect(state).toEqual({
      branch: "",
      error: "ACP authentication was rejected. Run 'acp login' and try again.",
      manual: false,
      status: "error",
    });
  });
});
