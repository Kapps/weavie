import { describe, expect, it, vi } from "vitest";
import { CommandIds } from "../commands/types";
import { sessionMenuEntries } from "./session-menu";
import type { RailSession } from "./session-store";

vi.mock("../commands/registry", () => ({ getKeybindings: () => [], onCommandsChanged: () => {} }));

function railSession(over: Partial<RailSession>): RailSession {
  return {
    owner: null,
    id: "s1",
    label: "feature/x",
    active: false,
    loaded: true,
    providerId: "claude",
    agentSurface: "terminal",
    agentInputProtocol: 0,
    status: "idle",
    title: "x",
    activity: null,
    run: null,
    workflowProblem: "",
    lastActiveAt: null,
    unseen: false,
    reloadRequired: null,
    external: false,
    repo: "/repo",
    repoLabel: "",
    backendId: "local",
    locationName: "default",
    isLocal: true,
    pending: false,
    offline: false,
    ...over,
  };
}

describe("sessionMenuEntries", () => {
  it("offers Unload for a loaded session and Load for a dormant one", () => {
    expect(sessionMenuEntries(railSession({ loaded: true }))[0]).toMatchObject({
      commandId: CommandIds.unloadSession,
    });
    expect(sessionMenuEntries(railSession({ loaded: false }))[0]).toMatchObject({
      commandId: CommandIds.loadSession,
    });
  });

  it("always offers a danger Delete… carrying the session id + backend", () => {
    const entries = sessionMenuEntries(railSession({ id: "abc", backendId: "remote:bob" }));
    expect(entries).toContainEqual({
      commandId: CommandIds.deleteSessionPrompt,
      args: { id: "abc", backendId: "remote:bob" },
      label: "Delete…",
      danger: true,
    });
  });
});
