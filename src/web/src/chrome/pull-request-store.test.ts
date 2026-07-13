import { beforeEach, describe, expect, it, vi } from "vitest";

type SessionMessage = { type: string; [key: string]: unknown };
const listeners = vi.hoisted(
  () => [] as Array<(message: SessionMessage, backendId: string) => void>,
);
vi.mock("../bridge", () => ({
  onSessionMessage: (listener: (message: SessionMessage, backendId: string) => void) => {
    listeners.push(listener);
    return () => {};
  },
}));

const store = await import("./pull-request-store");
const deliver = (backendId: string, message: SessionMessage): void => {
  for (const listener of listeners) listener(message, backendId);
};

describe("pull-request-store", () => {
  beforeEach(() => {
    deliver("local", {
      type: "pull-request-status",
      slot: "main",
      branch: "main",
      pullRequest: null,
      error: null,
    });
  });

  it("keeps pull request status isolated by backend and slot", () => {
    const pullRequest = { number: 123, url: "https://github.com/Kapps/weavie/pull/123" };
    deliver("remote", {
      type: "pull-request-status",
      slot: "feature",
      branch: "feat/native-ui-pr",
      pullRequest,
      error: null,
    });

    expect(store.pullRequestStatus("remote", "feature")).toEqual({
      branch: "feat/native-ui-pr",
      pullRequest,
      error: null,
    });
    expect(store.pullRequestStatus("local", "feature")).toBeNull();
    expect(store.pullRequestStatus("remote", "main")).toBeNull();
  });
});
