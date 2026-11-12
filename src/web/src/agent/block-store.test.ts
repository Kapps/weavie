import { describe, expect, it, vi } from "vitest";
import type { AgentBlockRecord, ClientSession } from "../bridge";

vi.mock("../messaging/session-owned-state", () => ({
  createSessionOwnedResource: <T>(create: (session: ClientSession) => T) => {
    const states = new Map<ClientSession, T>();
    return {
      get: (session: ClientSession) => {
        if (!states.has(session)) states.set(session, create(session));
        return states.get(session);
      },
    };
  },
}));

const { agentBlock, putBlocks, tableView } = await import("./block-store");
const { blockKey } = await import("./AgentPaneIdentity");

const record = (generation: number, revision: number, title: string): AgentBlockRecord => ({
  generation,
  revision,
  providerId: "fake",
  conversationId: null,
  block: { id: title === "other" ? "other" : "p95", title },
});

describe("block store", () => {
  it("keeps the newest revision and lets a new generation void the old one's blocks", () => {
    const session = {} as ClientSession;
    const key = blockKey("fake", null, "p95");
    putBlocks(session, [record(1, 40, "later"), record(1, 10, "older"), record(1, 41, "other")]);
    expect(agentBlock(session, key)?.block.title).toBe("later");

    putBlocks(session, [record(2, 3, "after clear")]);
    expect(agentBlock(session, key)?.block.title).toBe("after clear");
    expect(agentBlock(session, blockKey("fake", null, "other"))).toBeUndefined();

    putBlocks(session, [record(1, 99, "stale")]);
    expect(agentBlock(session, key)?.block.title).toBe("after clear");
  });

  it("keeps a table's view with its block until a new generation voids the block", () => {
    const session = {} as ClientSession;
    const key = blockKey("fake", null, "p95");
    putBlocks(session, [record(1, 1, "first")]);
    tableView(session, key)!.setSort({ column: 1, descending: true });

    expect(tableView(session, key)!.sort()).toEqual({ column: 1, descending: true });
    putBlocks(session, [record(2, 1, "next")]);
    expect(tableView(session, key)!.sort()).toBeNull();
  });
});
