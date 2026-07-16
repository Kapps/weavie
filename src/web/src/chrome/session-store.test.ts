import { beforeEach, describe, expect, it, vi } from "vitest";
import type { RailSession } from "./session-store";

// NOTE: the store's working-set views (`sessions`, `railSessions`, `remoteAgentRows`) are module-scope Solid
// memos. Outside a render root they never track their sources, so they can't be exercised in this pure-node
// env — that reactive behaviour is covered by the Playwright e2e suite (e2e/functional/session.spec.ts).
// `claudeStatus` is a plain signal, so its host-sync gating IS unit-testable here.

type SessionMsg = { type: string; [k: string]: unknown };
const handlers = vi.hoisted(() => [] as Array<(m: SessionMsg, backendId: string) => void>);
vi.mock("../bridge", () => ({
  // The page is bound to the local backend throughout these tests.
  activeBackendId: () => "local",
  editorBackendId: () => null,
  editorRailSessionId: () => null,
  editorSessionId: () => null,
  backendName: (id: string) => id,
  connectedBackends: () => [{ id: "local", name: "default", isLocal: true }],
  onSessionMessage: (h: (m: SessionMsg, backendId: string) => void) => {
    handlers.push(h);
    return () => {};
  },
  onHostMessage: () => () => {},
  onBackendDisconnected: () => () => {},
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

  it("adopts the waiting status (idle but resuming on a scheduled task)", () => {
    deliver({ type: "session-status", session: "claude", status: "waiting" }, "local");
    expect(store.claudeStatus()).toBe("waiting");
  });
});

const chip = (id: string, active: boolean): RailSession => ({
  id,
  label: id,
  active,
  loaded: true,
  primary: false,
  providerId: "claude",
  status: "idle",
  hue: 0,
  monogram: id.slice(0, 1),
  backendId: "local",
  locationName: "default",
  isLocal: true,
  pending: false,
});

describe("stepRailTarget", () => {
  it("returns null when there's nothing to move to", () => {
    expect(store.stepRailTarget([], 1)).toBeNull();
    expect(store.stepRailTarget([], -1)).toBeNull();
    // A lone active chip has no sibling to cycle to, so the keystroke falls through.
    expect(store.stepRailTarget([chip("a", true)], 1)).toBeNull();
    expect(store.stepRailTarget([chip("a", true)], -1)).toBeNull();
  });

  it("steps to the next/prev chip from the active one, wrapping the ends", () => {
    const list = [chip("a", false), chip("b", true), chip("c", false)];
    expect(store.stepRailTarget(list, 1)?.id).toBe("c");
    expect(store.stepRailTarget(list, -1)?.id).toBe("a");
    expect(store.stepRailTarget([chip("a", true), chip("b", false)], -1)?.id).toBe("b");
    expect(store.stepRailTarget([chip("a", false), chip("b", true)], 1)?.id).toBe("a");
  });

  // The regression: after deleting the focused session the page can be left with no active rail chip. Cycling
  // must then recover focus to the near end (first for next, last for prev) rather than dead-key.
  it("recovers focus to the near end when no chip is active", () => {
    expect(store.stepRailTarget([chip("a", false)], 1)?.id).toBe("a");
    expect(store.stepRailTarget([chip("a", false)], -1)?.id).toBe("a");
    const list = [chip("a", false), chip("b", false), chip("c", false)];
    expect(store.stepRailTarget(list, 1)?.id).toBe("a");
    expect(store.stepRailTarget(list, -1)?.id).toBe("c");
  });
});
