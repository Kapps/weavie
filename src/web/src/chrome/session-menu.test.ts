import { describe, expect, it } from "vitest";
import { CommandIds } from "../commands/types";
import { sessionMenuEntries } from "./session-menu";
import type { RailSession } from "./session-store";

function railSession(over: Partial<RailSession>): RailSession {
  return {
    id: "s1",
    label: "feature/x",
    active: false,
    loaded: true,
    primary: false,
    status: "idle",
    hue: 120,
    monogram: "FX",
    backendId: "local",
    locationName: "default",
    isLocal: true,
    pending: false,
    ...over,
  };
}

describe("sessionMenuEntries", () => {
  it("offers Unload for a loaded session and Load for a dormant one", () => {
    expect(sessionMenuEntries(railSession({ loaded: true }), false)[0]).toMatchObject({
      commandId: CommandIds.unloadSession,
    });
    expect(sessionMenuEntries(railSession({ loaded: false }), false)[0]).toMatchObject({
      commandId: CommandIds.loadSession,
    });
  });

  it("always offers a danger Delete… carrying the session id + backend", () => {
    const entries = sessionMenuEntries(railSession({ id: "abc", backendId: "remote:bob" }), false);
    expect(entries).toContainEqual({
      commandId: CommandIds.deleteSessionPrompt,
      args: { id: "abc", backendId: "remote:bob" },
      label: "Delete…",
      danger: true,
    });
  });

  it("does not offer Remove from rail for a local session", () => {
    const entries = sessionMenuEntries(railSession({ isLocal: true }), true);
    expect(entries.some((e) => "commandId" in e && e.commandId === CommandIds.removeFromRail)).toBe(
      false,
    );
  });

  it("offers Remove from rail only for a remote session already in the rail", () => {
    expect(
      sessionMenuEntries(railSession({ isLocal: false }), false).some(
        (e) => "commandId" in e && e.commandId === CommandIds.removeFromRail,
      ),
    ).toBe(false);

    const inRail = sessionMenuEntries(
      railSession({ isLocal: false, backendId: "remote:b", id: "z" }),
      true,
    );
    expect(inRail).toContainEqual({
      commandId: CommandIds.removeFromRail,
      args: { backendId: "remote:b", id: "z" },
      label: "Remove from rail",
    });
  });
});
