import { describe, expect, it, vi } from "vitest";
import type { SessionRun } from "../bridge";
import type { RailSession } from "./session-store";
import {
  panelOrder,
  runStatus,
  sessionDetail,
  sessionGroup,
  sessionStatusLabel,
  sessionSubtitle,
  sessionTooltip,
  statusDot,
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

  it("groups workflow attention consistently with its status icon", () => {
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
    expect(sessionGroup(session({ run: run("waitingOnYou") }))).toBe("needs");
    expect(sessionGroup(session({ run: run("stalled") }))).toBe("needs");
    for (const state of ["needsAnswer", "paused", "waitingOnYou"] as const) {
      expect(statusDot(session({ run: run(state), unseen: true }))).toBe("needsInput");
    }
    expect(statusDot(session({ run: run("watching") }))).toBe("waiting");
    expect(statusDot(session({ run: run("deciding") }))).toBe("working");
    expect(statusDot(session({ status: "error", run: run("working") }))).toBe("error");
    expect(statusDot(session({ status: "needsInput", run: run("working") }))).toBe("needsInput");
    expect(statusDot(session({ reloadRequired: "Reload", run: run("working") }))).toBe("error");
    expect(statusDot(session({ loaded: false, run: run("needsAnswer") }))).toBe("unloaded");
    expect(statusDot(session({ offline: true, run: run("needsAnswer") }))).toBe("offline");
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
  it.each([
    "needsAnswer",
    "paused",
    "waitingOnYou",
  ] as const)("shows active work over %s and restores workflow attention when it rests", (state) => {
    const run: SessionRun = {
      id: "r",
      workflow: "flow",
      stage: "design",
      state,
      reason: "Approve the design?",
      agent: "",
    };
    for (const [status, label] of [
      ["working", "Working"],
      ["starting", "Starting"],
      ["waiting", "Background work"],
      ["needsInput", "Needs input"],
      ["error", "Error"],
    ] as const) {
      const current = session({ status, run });
      expect(statusDot(current)).toBe(status);
      expect(sessionStatusLabel(current)).toBe(label);
      expect(sessionGroup(current)).toBe(
        status === "needsInput" || status === "error" ? "needs" : "working",
      );
      expect(sessionDetail(current)).toBe(run.reason);
      if (status === "working" || status === "starting" || status === "waiting") {
        expect(sessionSubtitle(current, NOW)).toBe(`design · ${label.toLowerCase()}`);
        expect(sessionTooltip(current)).toBe(
          `flow · design · ${label.toLowerCase()}\n${run.reason}`,
        );
      }
    }
    const resting = session({ status: "idle", run, unseen: true });
    expect(sessionGroup(resting)).toBe("needs");
    expect(statusDot(resting)).toBe("needsInput");
    expect(sessionStatusLabel(resting)).toBe("Needs input");
  });

  it("maps workflow progress and user attention to shared session statuses", () => {
    expect(runStatus("working")).toBe("working");
    expect(runStatus("deciding")).toBe("working");
    expect(runStatus("watching")).toBe("waiting");
    for (const state of ["waitingOnYou", "needsAnswer", "paused"] as const) {
      expect(runStatus(state)).toBe("needsInput");
    }
    expect(runStatus("stalled")).toBe("error");
  });

  it("keeps workflow text, header and footer aligned with overriding session states", () => {
    const run: SessionRun = {
      id: "r",
      workflow: "flow",
      stage: "build",
      state: "working",
      reason: "",
      agent: "",
    };
    for (const [patch, status, label, text] of [
      [{ status: "needsInput" }, "needsInput", "Needs input", "needs input"],
      [{ status: "error" }, "error", "Error", "error"],
      [{ offline: true }, "offline", "Connection lost", "connection lost"],
      [{ reloadRequired: "Reload to recover" }, "error", "Needs reload", "needs reload"],
    ] as const) {
      const current = session({ run, ...patch });
      expect(statusDot(current)).toBe(status);
      expect(sessionStatusLabel(current)).toBe(label);
      expect(sessionTooltip(current)).toContain(`flow · build · ${text}`);
      expect(sessionSubtitle(current, NOW)).toContain(text);
    }
    const waiting = session({ run: { ...run, state: "waitingOnYou" }, unseen: true });
    expect(sessionStatusLabel(waiting)).toBe("Needs input");
    expect(sessionTooltip(waiting)).toBe("flow · build · waiting on you");
    expect(sessionStatusLabel(session({ status: "waiting" }))).toBe("Background work");
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
    expect(sessionTooltip(session({ run }))).toBe("ship-task · review · needs your answer");
    expect(sessionTooltip(session({ run: { ...run, reason: "Anything blocking?" } }))).toBe(
      "ship-task · review · needs your answer\nAnything blocking?",
    );
    expect(sessionTooltip(session({ run: { ...run, agent: "reviewer" } }))).toBe(
      "ship-task · review on reviewer · needs your answer",
    );
  });
});
