import { describe, expect, it, vi } from "vitest";
import type { RailSession } from "./session-store";
import { sessionGroup, sessionSubtitle } from "./session-summary";

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
  lastActiveAt: null,
  workspaceCheckout: false,
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
});
