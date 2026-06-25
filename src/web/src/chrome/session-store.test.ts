import { beforeEach, describe, expect, it, vi } from "vitest";

// NOTE: the store's working-set views (`sessions`, `railSessions`, `remoteAgentRows`) are module-scope Solid
// memos. Outside a render root they never track their sources, so they can't be exercised in this pure-node
// env — that reactive behaviour is covered by the Playwright e2e suite (e2e/functional/session.spec.ts).
// `claudeStatus` is a plain signal, so its host-sync gating IS unit-testable here.

type SessionMsg = { type: string; [k: string]: unknown };
const handlers = vi.hoisted(() => [] as Array<(m: SessionMsg, backendId: string) => void>);
vi.mock("../bridge", () => ({
  // The page is bound to the local backend throughout these tests.
  activeBackendId: () => "local",
  backendName: (id: string) => id,
  connectedBackends: () => [{ id: "local", name: "default", isLocal: true }],
  onSessionMessage: (h: (m: SessionMsg, backendId: string) => void) => {
    handlers.push(h);
    return () => {};
  },
  postToBackend: () => {},
  connectBackend: () => {},
  disconnectBackend: () => {},
  log: () => {},
}));

const store = await import("./session-store");

const deliver = (message: SessionMsg, backendId: string): void => {
  for (const h of handlers) {
    h(message, backendId);
  }
};

beforeEach(() => {
  // Reset to a known status between tests.
  deliver({ type: "session-status", session: "claude", status: "starting" }, "local");
});

describe("claudeStatus host sync", () => {
  it("adopts the active backend's claude status", () => {
    deliver({ type: "session-status", session: "claude", status: "working" }, "local");
    expect(store.claudeStatus()).toBe("working");
  });

  it("ignores a status from a non-active backend (no cross-backend leak)", () => {
    deliver({ type: "session-status", session: "claude", status: "working" }, "local");
    deliver({ type: "session-status", session: "claude", status: "idle" }, "remote:r");
    expect(store.claudeStatus()).toBe("working");
  });

  it("ignores the shell pane's status — only claude drives the dot", () => {
    deliver({ type: "session-status", session: "claude", status: "needsInput" }, "local");
    deliver({ type: "session-status", session: "shell", status: "idle" }, "local");
    expect(store.claudeStatus()).toBe("needsInput");
  });
});
