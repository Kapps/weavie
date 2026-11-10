import { describe, expect, it, vi } from "vitest";
import type { SessionRun } from "../bridge";
import type { RailSession } from "./session-store";
import {
  panelOrder,
  runDotColor,
  runTooltip,
  sessionDetail,
  sessionGroup,
  sessionSubtitle,
} from "./session-summary";

vi.mock("../commands/registry", () => ({ getKeybindings: () => [], onCommandsChanged: () => {} }));

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
  workflowProblem: "",
  lastActiveAt: null,
  unseen: false,
  workspaceCheckout: false,
  external: false,
  reloadRequired: null,
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
    expect(sessionGroup(session({ status: "idle", unseen: true }))).toBe("finished");
    expect(sessionGroup(session({ status: "working", unseen: true }))).toBe("working");
    expect(sessionGroup(session({ loaded: false, status: "needsInput" }))).toBe("parked");
  });

  it("puts a run awaiting an answer or paused under Needs you, but not an agent waiting for a reply", () => {
    const run = (state: SessionRun["state"]): SessionRun => ({
      id: "r",
      workflow: "quick-fix",
      stage: "implement",
      state,
      reason: "",
      agent: "",
    });
    expect(sessionGroup(session({ run: run("needsAnswer") }))).toBe("needs");
    expect(sessionGroup(session({ run: run("paused") }))).toBe("needs");
    expect(sessionGroup(session({ run: run("waitingOnYou") }))).toBe("idle");
    expect(sessionGroup(session({ status: "working", run: run("working") }))).toBe("working");
    expect(sessionGroup(session({ loaded: false, run: run("needsAnswer") }))).toBe("parked");
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
      session({ id: "finished", unseen: true }),
    ];
    expect(panelOrder(sessions).map((s) => s.id)).toEqual([
      "needs",
      "working",
      "finished",
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
    expect(sessionSubtitle(session({ unseen: true, lastActiveAt: NOW - 3 * 60_000 }), NOW)).toBe(
      "finished · 3m ago",
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
      reason: "",
      agent: "",
    };
    expect(
      sessionSubtitle(session({ status: "working", activity: "running pnpm test", run }), NOW),
    ).toBe("implement · running pnpm test");
    expect(sessionSubtitle(session({ status: "working", run }), NOW)).toBe("implement · working");
    expect(sessionSubtitle(session({ run: { ...run, state: "needsAnswer" } }), NOW)).toBe(
      "implement · needs your answer",
    );
    expect(sessionSubtitle(session({ loaded: false, run }), NOW)).toBe("not loaded");
  });

  it("marks a worktree Weavie didn't create", () => {
    expect(sessionSubtitle(session({ loaded: false, external: true }), NOW)).toBe(
      "outside Weavie · not loaded",
    );
  });

  it("keeps the line short and puts why a run paused, or why it couldn't load, in the detail", () => {
    const run: SessionRun = {
      id: "r",
      workflow: "quick-fix",
      stage: "pr",
      state: "paused",
      reason: 'The agent couldn\'t finish stage "pr": no remote',
      agent: "",
    };
    expect(sessionSubtitle(session({ run }), NOW)).toBe("pr · paused");
    expect(sessionDetail(session({ run }))).toBe(run.reason);
    const unreadable = session({ workflowProblem: "The saved workflow run couldn't be read" });
    expect(sessionSubtitle(unreadable, NOW)).toBe("workflow run unreadable");
    expect(sessionDetail(unreadable)).toBe("The saved workflow run couldn't be read");
    expect(sessionDetail(session({ loaded: false, run }))).toBe("");
  });
});

describe("run header", () => {
  it("colors the dot by whether the run needs the user", () => {
    const colors = (states: SessionRun["state"][]): string[] => states.map(runDotColor);
    expect(colors(["working", "deciding", "watching"])).toEqual(Array(3).fill("var(--busy)"));
    expect(runDotColor("waitingOnYou")).toBe("var(--ok)");
    expect(colors(["needsAnswer", "paused"])).toEqual(Array(2).fill("var(--warn)"));
    expect(runDotColor("stalled")).toBe("var(--bad)");
  });

  it("spells out the run with its stage's own agent, adding its reason on a second line", () => {
    const run: SessionRun = {
      id: "r",
      workflow: "ship-task",
      stage: "review",
      state: "needsAnswer",
      reason: "",
      agent: "",
    };
    expect(runTooltip(run)).toBe("ship-task · review · needs your answer");
    expect(runTooltip({ ...run, reason: "Anything blocking?" })).toBe(
      "ship-task · review · needs your answer\nAnything blocking?",
    );
    expect(runTooltip({ ...run, agent: "reviewer" })).toBe(
      "ship-task · review on reviewer · needs your answer",
    );
  });
});
