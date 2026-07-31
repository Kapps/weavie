import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";

vi.mock("solid-js", () => import(["solid-js", "dist/solid.js"].join("/")));

const harness = vi.hoisted(() => ({
  installer: undefined as ((session: ClientSession) => undefined | (() => void)) | undefined,
  sessions: new Map<
    string,
    {
      client: ClientSession;
      deliver?: (payload: Record<string, unknown>) => void;
    }
  >(),
}));

vi.mock("../bridge", () => ({
  registerSessionFeature: (installer: (session: ClientSession) => undefined | (() => void)) => {
    harness.installer = installer;
    return () => {};
  },
}));

const store = await import("./pull-request-store");

function session(
  backendId: string,
  slot: string,
): {
  client: ClientSession;
  deliver?: (payload: Record<string, unknown>) => void;
} {
  const key = `${backendId}\u0000${slot}`;
  const existing = harness.sessions.get(key);
  if (existing !== undefined) {
    return existing;
  }
  const created = {} as {
    client: ClientSession;
    deliver?: (payload: Record<string, unknown>) => void;
  };
  created.client = {
    connection: { id: backendId },
    address: { slot, incarnation: `${slot}-incarnation` },
    feature: () => ({
      on: (_name: string, handler: (payload: Record<string, unknown>) => void) => {
        created.deliver = handler;
        return () => {};
      },
    }),
  } as unknown as ClientSession;
  harness.sessions.set(key, created);
  harness.installer?.(created.client);
  return created;
}

const deliver = (backendId: string, slot: string, payload: Record<string, unknown>): void =>
  session(backendId, slot).deliver?.(payload);

describe("pull-request-store", () => {
  beforeEach(() => {
    deliver("local", "main", {
      branch: "main",
      pullRequest: null,
      error: null,
    });
  });

  it("keeps pull request status isolated by backend and slot", () => {
    const pullRequest = { number: 123, url: "https://github.com/Kapps/weavie/pull/123" };
    deliver("remote", "feature", {
      branch: "feat/native-ui-pr",
      pullRequest,
      error: null,
    });

    expect(store.pullRequestStatus(session("remote", "feature").client)).toEqual({
      branch: "feat/native-ui-pr",
      pullRequest,
      error: null,
    });
    expect(store.pullRequestStatus(session("local", "feature").client)).toBeNull();
    expect(store.pullRequestStatus(session("remote", "main").client)).toBeNull();
  });
});
