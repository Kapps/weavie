import { createRoot } from "solid-js";
import { describe, expect, it, vi } from "vitest";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { paneItemIdentity } from "./AgentPaneIdentity";
import { createAgentPaneModel } from "./AgentPaneModel";
import { share, withLivePending } from "./AgentTranscriptSnapshot";

vi.mock("../bridge", () => ({ registerSessionFeature: () => () => {} }));

describe("transcript snapshots", () => {
  it("reuses every unchanged part, matching array items by id", () => {
    const previous = {
      entries: [
        { id: "a", text: "one", details: [{ id: "s1", label: "x" }] },
        { id: "b", text: "two", asideEntries: [{ id: "c", text: "three" }] },
      ],
    };
    const next = structuredClone(previous);
    expect(share(previous, next)).toBe(previous);

    next.entries[1]!.asideEntries![0]!.text = "changed";
    next.entries.unshift({ id: "new", text: "new", details: [] });
    const shared = share(previous, next);
    expect(shared).not.toBe(previous);
    expect(shared.entries[1]).toBe(previous.entries[0]);
    expect(shared.entries[2]).not.toBe(previous.entries[1]);
    expect(shared.entries[2]!.text).toBe(previous.entries[1]!.text);
    expect(shared.entries[2]!.asideEntries![0]!.text).toBe("changed");
  });

  it("returns the previous snapshot when a publish changes nothing", () => {
    createRoot((dispose) => {
      const model = createAgentPaneModel({} as ClientSession);
      const updates = [user("prompt"), answer("first")];
      model.replace(updates);
      const before = model.transcript();
      model.publish([...updates], []);
      expect(model.transcript()).toBe(before);
      dispose();
    });
  });

  it("leaves a held snapshot untouched while later output is published", () => {
    createRoot((dispose) => {
      const model = createAgentPaneModel({} as ClientSession);
      const updates: AgentPaneUpdate[] = [user("prompt"), command("c0"), answer("first")];
      model.replace(updates);
      const held = model.transcript();
      const frozen = structuredClone(held.entries);
      for (let index = 1; index < 20; index++) {
        updates.push(command(`c${index}`), answer(`reply ${index}`));
        model.publish([...updates], updates.slice(-2));
      }
      model.setActivityExpanded(held.entries[1]!.id, true);
      expect(held.entries).toEqual(frozen);
      const live = model.transcript();
      expect(live.entries[0]).toBe(held.entries[0]);
      expect(live.entries.length).toBeGreaterThan(held.entries.length);
      dispose();
    });
  });

  it("path-copies an activity update so its siblings keep their identity", () => {
    createRoot((dispose) => {
      const model = createAgentPaneModel({} as ClientSession);
      const updates: AgentPaneUpdate[] = [user("prompt"), answer("first"), command("c0")];
      model.replace(updates);
      const before = model.transcript();
      const next = command("c1");
      updates.push(next);
      model.publish([...updates], [next]);
      const after = model.transcript();
      expect(after.entries[0]).toBe(before.entries[0]);
      expect(after.entries[1]).toBe(before.entries[1]);
      expect(after.entries[2]).not.toBe(before.entries[2]);
      expect(after.entries[2]!.detailCount).toBe(2);
      expect(before.entries[2]!.detailCount).toBe(1);
      dispose();
    });
  });

  it("shows live pending requests in a held snapshot", () => {
    createRoot((dispose) => {
      const model = createAgentPaneModel({} as ClientSession);
      const first = request("first");
      const updates: AgentPaneUpdate[] = [user("prompt"), answer("history"), first];
      model.replace(updates);
      const held = model.transcript();
      expect(withLivePending(held, held)).toBe(held);

      const second = request("second");
      updates.push(answer("later"), { ...first, type: "input-resolved", status: "accepted" });
      updates.push(second);
      model.publish([...updates], updates.slice(-3));
      const merged = withLivePending(held, model.transcript());
      expect(merged.entries.map((entry) => entry.id)).toEqual([
        held.entries[0]!.id,
        held.entries[1]!.id,
        paneItemIdentity(second),
      ]);
      expect(merged.entries[0]).toBe(held.entries[0]);
      expect(merged.pendingRowIndexes).toEqual([2]);
      expect(withLivePending(held, model.transcript()).entries).toEqual(merged.entries);
      dispose();
    });
  });
});

function user(text: string): AgentPaneUpdate {
  return { type: "user-message", providerId: "acp", turnId: "turn", text };
}

function answer(itemId: string): AgentPaneUpdate {
  return {
    type: "item-completed",
    providerId: "acp",
    turnId: "turn",
    itemId,
    itemType: "agentMessage",
    status: "completed",
    text: itemId,
  };
}

function command(itemId: string): AgentPaneUpdate {
  return {
    type: "item-completed",
    providerId: "acp",
    turnId: "turn",
    itemId,
    itemType: "commandExecution",
    status: "completed",
    summary: itemId,
  };
}

function request(id: string): AgentPaneUpdate {
  return {
    type: "input-requested",
    providerId: "acp",
    threadId: "thread",
    turnId: "turn",
    itemId: id,
    requestId: id,
    status: "pending",
  };
}
