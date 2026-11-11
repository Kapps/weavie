import { createRoot } from "solid-js";
import { describe, expect, it, vi } from "vitest";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { paneItemIdentity } from "./AgentPaneIdentity";
import { createTestAgentPaneModel as createAgentPaneModel } from "./AgentPaneTestModel";

type MutableAgentPaneModel = ReturnType<typeof createAgentPaneModel>;

import type { AgentTranscriptEntry, AgentTranscriptSnapshot } from "./AgentPaneTranscriptTypes";
import { emptyTranscript, share, withLivePending } from "./AgentTranscriptSnapshot";

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
    withModel((model) => {
      const updates = [user("prompt"), answer("first")];
      model.replace(updates);
      const before = model.transcript();
      model.publish([...updates], []);
      expect(model.transcript()).toBe(before);
    });
  });

  it("leaves a held snapshot untouched while later output is published", () => {
    withModel((model) => {
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
    });
  });

  it("path-copies an activity update so its siblings keep their identity", () => {
    withModel((model) => {
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
    });
  });

  it("copies an expanded activity once per batch, keeping its untouched steps", () => {
    withModel((model) => {
      const updates: AgentPaneUpdate[] = [user("prompt"), answer("first"), command("c0")];
      model.replace(updates);
      const id = model.transcript().entries[2]!.id;
      model.setActivityExpanded(id, true);
      const before = model.transcript().entries[2]!;
      const batch = [command("c1"), command("c2")];
      updates.push(...batch);
      model.publish([...updates], batch);
      const after = model.transcript().entries[2]!;
      expect(after.detailCount).toBe(3);
      expect(after.details).toHaveLength(3);
      expect(after.details[0]).toBe(before.details[0]);
    });
  });

  it("counts output only when published output changes the latest row", () => {
    withModel((model) => {
      const updates: AgentPaneUpdate[] = [user("prompt"), command("c0")];
      model.replace(updates);
      const start = model.latestOutput();
      model.setActivityExpanded(model.transcript().entries[1]!.id, true);
      expect(model.latestOutput()).toBe(start);
      model.publish([...updates], []);
      expect(model.latestOutput()).toBe(start);
      updates.push(answer("reply"));
      model.publish([...updates], updates.slice(-1));
      expect(model.latestOutput()).toBe(start + 1);
    });
  });

  it("shows a held snapshot's requests live, resolved in place", () => {
    withModel((model) => {
      const first = request("first");
      const updates: AgentPaneUpdate[] = [user("prompt"), answer("history"), first];
      model.replace(updates);
      const held = model.transcript();
      expect(withLivePending(held, held)).toBe(held);

      const second = request("second");
      updates.push(answer("later"), { ...first, type: "input-resolved", status: "accepted" });
      updates.push(second);
      model.publish([...updates], updates.slice(-3));
      const live = model.transcript();
      const merged = withLivePending(held, live);
      expect(merged.entries.map((entry) => entry.id)).toEqual([
        held.entries[0]!.id,
        held.entries[1]!.id,
        paneItemIdentity(first),
        paneItemIdentity(second),
      ]);
      expect(merged.entries[0]).toBe(held.entries[0]);
      expect(merged.entries[2]).toBe(
        live.entries.find((entry) => entry.id === held.entries[2]!.id),
      );
      expect(merged.entries[2]!.status).not.toBe("pending");
      expect(merged.pendingRowIndexes).toEqual([3]);
      expect(withLivePending(held, live).entries).toEqual(merged.entries);
    });
  });

  it("shows a card's nested requests live and drops a request live no longer has", () => {
    const pending = entry("request", { kind: "request", status: "pending" });
    const card = entry("card", { kind: "subagent", asideEntries: [entry("step", {}), pending] });
    const quiet = entry("quiet", { kind: "subagent", asideEntries: [entry("note", {})] });
    const held = snapshot([entry("a", {}), card, quiet, entry("gone", pending)]);
    const resolved = {
      ...card,
      asideEntries: [entry("step", {}), { ...pending, status: "accepted" }],
    };
    const asking = { ...quiet, asideEntries: [entry("note", {}), entry("ask", pending)] };
    const live = snapshot([entry("a", {}), resolved, asking, entry("later", {})]);
    const merged = withLivePending(held, live);
    expect(merged.entries).toEqual([held.entries[0], resolved, asking]);
    expect(merged.entries[0]).toBe(held.entries[0]);
    expect(merged.pendingRowIndexes).toEqual([2]);
    expect(withLivePending(snapshot([entry("a", {}), quiet]), live).entries[1]).toBe(asking);
  });
});

function withModel(test: (model: MutableAgentPaneModel) => void): void {
  createRoot((dispose) => {
    test(createAgentPaneModel({} as ClientSession));
    dispose();
  });
}

function entry(id: string, fields: Partial<AgentTranscriptEntry>): AgentTranscriptEntry {
  return { ...({ kind: "message", status: null } as AgentTranscriptEntry), ...fields, id };
}

function snapshot(entries: AgentTranscriptEntry[]): AgentTranscriptSnapshot {
  return {
    ...emptyTranscript,
    entries,
    entryIndexes: new Map(entries.map((entry, index) => [entry.id, index])),
    pendingRowIndexes: entries.flatMap((entry, index) =>
      entry.status === "pending" || entry.asideEntries?.some((child) => child.status === "pending")
        ? [index]
        : [],
    ),
  };
}

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
