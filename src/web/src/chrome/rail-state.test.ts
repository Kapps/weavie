import { beforeEach, describe, expect, it, vi } from "vitest";

type SessionMsg = { type: string; [k: string]: unknown };
const posted = vi.hoisted(() => [] as Array<Record<string, unknown>>);
const sessionHandlers = vi.hoisted(() => [] as Array<(m: SessionMsg, backendId: string) => void>);
vi.mock("../bridge", () => ({
  onSessionMessage: (h: (m: SessionMsg, backendId: string) => void) => {
    sessionHandlers.push(h);
    return () => {};
  },
  postToLocalHost: (message: Record<string, unknown>) => {
    posted.push(message);
  },
}));

const rail = await import("./rail-state");

const deliver = (message: SessionMsg, backendId: string): void => {
  for (const h of sessionHandlers) {
    h(message, backendId);
  }
};
const chip = (id: string, active = false): Record<string, unknown> => ({ id, active });

beforeEach(() => {
  posted.length = 0;
  // A rail-state push from local resets the promoted set to a known-empty baseline.
  deliver(
    { type: "rail-state", lastLocation: "local", lastAgentProvider: "claude", promoted: [] },
    "local",
  );
  posted.length = 0;
});

describe("rail-state host sync", () => {
  it("adopts lastLocation + promoted from a local rail-state push", () => {
    deliver(
      {
        type: "rail-state",
        lastLocation: "remote:r",
        lastAgentProvider: "codex",
        promoted: ["remote:r s1"],
      },
      "local",
    );
    expect(rail.lastLocation()).toBe("remote:r");
    expect(rail.lastAgentProvider()).toBe("codex");
    expect(rail.isPromoted("remote:r", "s1")).toBe(true);
  });

  it("ignores a rail-state push from a non-local backend", () => {
    deliver(
      { type: "rail-state", lastLocation: "evil", lastAgentProvider: "codex", promoted: ["x y"] },
      "remote:r",
    );
    expect(rail.lastLocation()).not.toBe("evil");
    expect(rail.isPromoted("x", "y")).toBe(false);
  });
});

describe("promote / demote", () => {
  it("promotes a remote session and pushes the new set to local", () => {
    rail.promoteSession("remote:a", "s1");
    expect(rail.isPromoted("remote:a", "s1")).toBe(true);
    expect(posted).toContainEqual({ type: "set-promoted", promoted: ["remote:a s1"] });
  });

  it("is idempotent: re-promoting pushes nothing new", () => {
    rail.promoteSession("remote:a", "s1");
    posted.length = 0;
    rail.promoteSession("remote:a", "s1");
    expect(posted).toEqual([]);
  });

  it("demotes a promoted session and pushes the shrunk set", () => {
    rail.promoteSession("remote:a", "s1");
    posted.length = 0;
    rail.demoteSession("remote:a", "s1");
    expect(rail.isPromoted("remote:a", "s1")).toBe(false);
    expect(posted).toContainEqual({ type: "set-promoted", promoted: [] });
  });

  it("demoting a non-promoted session is a no-op", () => {
    rail.demoteSession("remote:a", "ghost");
    expect(posted).toEqual([]);
  });
});

describe("setLastLocation", () => {
  it("updates the signal and tells the local backend", () => {
    rail.setLastLocation("remote:z");
    expect(rail.lastLocation()).toBe("remote:z");
    expect(posted).toContainEqual({ type: "set-last-location", location: "remote:z" });
  });
});

describe("setLastAgentProvider", () => {
  it("updates the signal and tells the local backend", () => {
    rail.setLastAgentProvider("codex");
    expect(rail.lastAgentProvider()).toBe("codex");
    expect(posted).toContainEqual({ type: "set-last-agent-provider", providerId: "codex" });
  });
});

describe("promoteNextSessionOn (one-shot auto-promote)", () => {
  it("promotes the genuinely new session in the next session-list, preferring the active one", () => {
    // A prior list establishes the known ids on this backend.
    deliver({ type: "session-list", sessions: [chip("a")] }, "remote:n1");
    rail.promoteNextSessionOn("remote:n1");
    deliver({ type: "session-list", sessions: [chip("a"), chip("b", true)] }, "remote:n1");
    expect(rail.isPromoted("remote:n1", "b")).toBe(true);
    expect(rail.isPromoted("remote:n1", "a")).toBe(false);
  });

  it("waits for a list that actually contains a new id rather than consuming the one-shot early", () => {
    deliver({ type: "session-list", sessions: [chip("a")] }, "remote:n2");
    rail.promoteNextSessionOn("remote:n2");
    deliver({ type: "session-list", sessions: [chip("a")] }, "remote:n2"); // no new id yet
    deliver({ type: "session-list", sessions: [chip("a"), chip("b")] }, "remote:n2");
    expect(rail.isPromoted("remote:n2", "b")).toBe(true);
  });

  it("is one-shot: a later new session is not auto-promoted", () => {
    rail.promoteNextSessionOn("remote:n3");
    deliver({ type: "session-list", sessions: [chip("b", true)] }, "remote:n3");
    expect(rail.isPromoted("remote:n3", "b")).toBe(true);
    deliver({ type: "session-list", sessions: [chip("b"), chip("c", true)] }, "remote:n3");
    expect(rail.isPromoted("remote:n3", "c")).toBe(false);
  });

  it("never arms for the local backend", () => {
    rail.promoteNextSessionOn("local");
    deliver({ type: "session-list", sessions: [chip("new", true)] }, "local");
    expect(rail.isPromoted("local", "new")).toBe(false);
  });
});
