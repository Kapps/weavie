import { describe, expect, it, vi } from "vitest";
import type { SessionRun } from "../bridge";
import type { RailSession } from "./session-store";
import { panelOrder, sessionGroup, sessionSubtitle } from "./session-summary";

vi.mock("../commands/registry", () => ({ getKeybindings: () => [] }));

const NOW = Date.UTC(2026, 9, 12, 12, 0);
const session = (patch: Partial<RailSession>): RailSession => ({
  owner: null,
  id: "s",
  label: "feature/review-front-door",
  title: "review front door",
  active: false,
  loaded: true,
  providerId: "claude",
  agentSurface: "terminal",
  agentInputProtocol: 0,
  status: "idle",
  activity: null,
  run: null,
  lastActiveAt: null,
  workspaceCheckout: false,
  external: false,
  backendId: "local",
  locationName: "default",
  isLocal: true,
  pending: false,
  offline: false,
  ...patch,
});

describe("sessionGroup", () => {
  it("puts what needs the user first and dormant sessions last", () => {
    expect(sessionGroup(session({ status: "needsInput" }))).toBe("needs");
    expect(sessionGroup(session({ status: "error" }))).toBe("needs");
    expect(sessionGroup(session({ status: "waiting" }))).toBe("working");
    expect(sessionGroup(session({ status: "idle" }))).toBe("idle");
    expect(sessionGroup(session({ loaded: false, status: "needsInput" }))).toBe("parked");
  });

  it("puts a run awaiting approval or paused under Needs you, but not an agent waiting for a reply", () => {
    const run = (state: SessionRun["state"]): SessionRun => ({
      id: "r",
      workflow: "quick-fix",
      stage: "implement",
      state,
    });
    expect(sessionGroup(session({ run: run("approval") }))).toBe("needs");
    expect(sessionGroup(session({ run: run("paused") }))).toBe("needs");
    expect(sessionGroup(session({ run: run("waitingOnYou") }))).toBe("idle");
    expect(sessionGroup(session({ status: "working", run: run("working") }))).toBe("working");
    expect(sessionGroup(session({ loaded: false, run: run("approval") }))).toBe("parked");
  });
});

describe("panelOrder", () => {
  it("groups like the panel, keeping rail order within a group", () => {
    const sessions = [
      session({ id: "idle-1" }),
      session({ id: "parked", loaded: false }),
      session({ id: "working", status: "working" }),
      session({ id: "idle-2" }),
      session({ id: "needs", status: "needsInput" }),
    ];
    expect(panelOrder(sessions).map((s) => s.id)).toEqual([
      "needs",
      "working",
      "idle-1",
      "idle-2",
      "parked",
    ]);
  });
});

describe("sessionSubtitle", () => {
  it("prefers the live activity", () => {
    expect(
      sessionSubtitle(session({ status: "working", activity: "running pnpm test" }), NOW),
    ).toBe("running pnpm test");
  });

  it("falls back to the state, with an age once the session is quiet", () => {
    expect(sessionSubtitle(session({ status: "working" }), NOW)).toBe("working");
    expect(sessionSubtitle(session({ activity: "running pnpm test" }), NOW)).toBe("idle");
    expect(sessionSubtitle(session({ lastActiveAt: NOW - 2 * 3_600_000 }), NOW)).toBe(
      "idle · 2h ago",
    );
    expect(sessionSubtitle(session({ loaded: false, activity: "stale" }), NOW)).toBe("not loaded");
    expect(sessionSubtitle(session({ offline: true }), NOW)).toBe("connection lost");
  });

  it("leads with the run's stage, then live activity or the run's state", () => {
    const run: SessionRun = {
      id: "r",
      workflow: "quick-fix",
      stage: "implement",
      state: "working",
    };
    expect(
      sessionSubtitle(session({ status: "working", activity: "running pnpm test", run }), NOW),
    ).toBe("implement · running pnpm test");
    expect(sessionSubtitle(session({ status: "working", run }), NOW)).toBe("implement · working");
    expect(sessionSubtitle(session({ run: { ...run, state: "approval" } }), NOW)).toBe(
      "implement · approval needs you",
    );
    expect(sessionSubtitle(session({ loaded: false, run }), NOW)).toBe("not loaded");
  });

  it("marks a worktree Weavie didn't create", () => {
    expect(sessionSubtitle(session({ loaded: false, external: true }), NOW)).toBe(
      "outside Weavie · not loaded",
    );
  });
});
