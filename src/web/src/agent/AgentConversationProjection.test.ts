import { createComputed, createRoot } from "solid-js";
import { describe, expect, it, vi } from "vitest";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { AgentPaneAccumulator } from "./AgentPaneAccumulator";
import type { AgentPaneConversationRows } from "./AgentPaneConversationRows";
import * as messages from "./AgentPaneMessages";
import { createAgentPaneModel } from "./AgentPaneModel";
import { holdTranscript, withLivePending } from "./AgentTranscriptSnapshot";

vi.mock(
  "solid-js",
  async () => await vi.importActual<typeof import("solid-js")>("solid-js/dist/solid.js"),
);
vi.mock("../bridge", () => ({ registerSessionFeature: () => () => {} }));

function marker(id: string, parent: string | null): AgentPaneUpdate {
  return {
    type: id === "btw" ? "side-conversation-started" : "subagent-started",
    providerId: "acp",
    conversationId: id,
    parentItemId: parent,
    summary: id,
    text: id,
  };
}
function answer(conversationId: string | null, itemId: string, text: string): AgentPaneUpdate {
  return {
    type: "item-completed",
    providerId: "acp",
    conversationId,
    threadId: conversationId ?? "main",
    turnId: "1",
    itemId,
    itemType: "agentMessage",
    text,
  };
}
function fixture(history: number, siblings: number) {
  const model = createAgentPaneModel(
    {} as ClientSession,
    () => "all",
    () => "all",
  );
  const accumulator = new AgentPaneAccumulator((callback) => {
    flush = callback;
  });
  let flush = () => {};
  let rows: AgentPaneConversationRows;
  const publish = (source: AgentPaneConversationRows, changes: AgentPaneUpdate[]) => {
    rows = source;
    model.publish(source, changes);
  };
  accumulator.ingestBatch(
    "pane",
    [
      ...Array.from({ length: history }, (_, index) =>
        answer(null, `main-${index}`, "Main history"),
      ),
      marker("btw", null),
      marker("child", "btw"),
      answer("child", "reply", "Initial"),
      ...Array.from({ length: siblings }, (_, index) => [
        marker(`sibling-${index}`, "btw"),
        answer(`sibling-${index}`, "reply", "Sibling"),
      ]).flat(),
    ],
    publish,
  );
  flush();
  const append = (...changes: AgentPaneUpdate[]) => {
    accumulator.ingestBatch("pane", changes, publish);
    flush();
  };
  return { model, append, rows: rows! };
}

describe("conversation projection locality", () => {
  it("upserts main activity with child cards, rebuilding only when membership changes", () =>
    createRoot((dispose) => {
      const { model, append } = fixture(20_000, 200);
      const tool = { ...answer(null, "tool", "first"), itemType: "tool", type: "item-started" };
      append({ ...tool, type: "turn-started", itemType: null, itemId: null }, tool);
      const project = vi.spyOn(messages, "projectAgentTranscript");
      const cards = model.transcript().entries.filter((entry) => entry.conversation);
      append({ ...tool, type: "command-output-delta", text: "next" });
      expect(project).not.toHaveBeenCalled();
      expect(model.transcript().entries.filter((entry) => entry.conversation)).toEqual(cards);
      append({ ...tool, type: "command-output-delta", text: "last" }, marker("new-child", null));
      expect(model.conversation("new-child")).toBeDefined();
      expect(model.subagents()).toContain(model.conversation("new-child"));
      expect(model.transcript().entries).not.toContain(model.conversation("new-child"));
      expect(project).toHaveBeenCalled();
      project.mockRestore();
      dispose();
    }));

  it("keeps row work independent of main history and siblings", () => {
    const measure = (history: number, siblings: number) =>
      createRoot((dispose) => {
        const { model, rows, append } = fixture(history, siblings);
        const child = model.conversation("child")!;
        const main = model.transcript();
        const mainOutput = model.latestOutput();
        const sibling = model.conversation("sibling-0")!.conversation!.transcript();
        const parentEntries = model.conversation("btw")!.asideEntries;
        let reads = 0;
        Object.defineProperty(rows, "messages", {
          value: new Proxy(rows.messages, {
            get(target, key, receiver) {
              if (typeof key === "string" && /^\d+$/.test(key)) reads += 1;
              return Reflect.get(target, key, receiver);
            },
          }),
        });
        const counts: number[] = [];
        const send = (...messages: AgentPaneUpdate[]) => {
          reads = 0;
          append(...messages);
          counts.push(reads);
          expect(model.transcript().entries).toBe(main.entries);
          expect(model.latestOutput()).toBe(mainOutput);
          expect(model.conversation("sibling-0")!.conversation!.transcript()).toBe(sibling);
          expect(model.conversation("btw")!.asideEntries).toBe(parentEntries);
        };
        send({ ...answer("child", "stream", "more"), type: "agent-message-delta" });
        send(answer("child", "stream", "Finished"));
        send({ ...answer("child", "tool", "output"), itemType: "tool", type: "item-started" });
        send({
          ...answer("child", "tool", "next"),
          itemType: "tool",
          type: "command-output-delta",
        });
        send({ ...answer("child", "tool", "done"), itemType: "tool" });
        const request = {
          ...answer("child", "approval", "Permit?"),
          type: "approval-requested",
          requestId: "approval",
          status: "pending",
        };
        send(request);
        expect(model.conversation("btw")!.pendingEntries).toHaveLength(0);
        expect(model.keyboardApprovalId()).toBe("approval");
        expect(child.pendingEntries).toHaveLength(1);
        send({ ...request, type: "approval-resolved", status: "accepted" });
        expect(child.pendingEntries).toHaveLength(0);
        send({ ...answer("child", "stream", ""), type: "item-retracted" });
        send({
          type: "turn-completed",
          providerId: "acp",
          conversationId: "child",
          status: "completed",
        });
        expect(child.subagent?.state).toBe("completed");
        expect(model.transcript().turnActive).toBe(false);
        dispose();
        return counts;
      });
    expect(measure(20_000, 200)).toEqual(measure(20, 2));
  });

  it("registers live descendants before their parent projects pending forms", () =>
    createRoot((dispose) => {
      const { model, append } = fixture(20, 2);
      append(
        { ...marker("new-btw", null), type: "side-conversation-started" },
        {
          type: "side-conversation-collapsed",
          providerId: "acp",
          conversationId: "new-btw",
          status: "collapsed",
        },
        marker("new-child", "new-btw"),
        {
          ...answer("new-child", "approval", "Permit?"),
          type: "approval-requested",
          requestId: "approval",
          status: "pending",
        },
      );
      const parent = model.conversation("new-btw")!;
      const child = model.conversation("new-child")!;
      expect(parent.conversation!.transcript().entryIndexes.has(child.id)).toBe(false);
      expect(parent.pendingEntries).toHaveLength(0);
      expect(child.pendingEntries).toHaveLength(1);
      expect(model.subagents()).toContain(child);
      expect(model.transcript().pendingRowIndexes).toHaveLength(0);
      dispose();
    }));

  it("places a descendant loaded before its parent announcement", () =>
    createRoot((dispose) => {
      const model = createAgentPaneModel(
        {} as ClientSession,
        () => "all",
        () => "all",
      );
      const accumulator = new AgentPaneAccumulator((callback) => {
        flush = callback;
      });
      let flush = () => {};
      const request = {
        ...answer("child", "approval", "Permit?"),
        type: "approval-requested",
        requestId: "approval",
        status: "pending",
      };
      accumulator.ingestBatch("pane", [marker("child", "btw"), request], model.publish);
      flush();
      expect(model.transcript().entries).toHaveLength(0);
      accumulator.ingest("pane", marker("btw", null), model.publish);
      flush();
      const parent = model.transcript().entries[0]!;
      expect(parent.id).toBe("aside-btw");
      const child = model.conversation("child")!;
      expect(parent.conversation!.transcript().entryIndexes.has(child.id)).toBe(false);
      expect(parent.pendingEntries).toHaveLength(0);
      expect(child.pendingEntries).toHaveLength(1);
      expect(model.subagents()).toContain(child);
      expect(model.transcript().pendingRowIndexes).toHaveLength(0);
      accumulator.ingest(
        "pane",
        { ...request, type: "approval-resolved", status: "accepted" },
        model.publish,
      );
      flush();
      expect(child.pendingEntries).toHaveLength(0);
      expect(parent.pendingEntries).toHaveLength(0);
      expect(model.transcript().pendingRowIndexes).toHaveLength(0);
      dispose();
    }));

  it("updates a tab opened before history arrives and removes it on reset", () =>
    createRoot((dispose) => {
      const model = createAgentPaneModel(
        {} as ClientSession,
        () => "all",
        () => "all",
      );
      const seen: Array<string | undefined> = [];
      createComputed(() => seen.push(model.conversation("child")?.id));
      const accumulator = new AgentPaneAccumulator((callback) => callback());
      accumulator.ingestBatch(
        "pane",
        [marker("child", null), answer("child", "reply", "Loaded")],
        model.publish,
      );
      expect(seen).toEqual([undefined, "subagent-child"]);
      model.reset();
      expect(seen).toEqual([undefined, "subagent-child", undefined]);
      dispose();
    }));

  it("keeps subagent requests out of parent transcript measurements", () =>
    createRoot((dispose) => {
      const { model, append } = fixture(20, 2);
      const child = model.conversation("child")!.conversation!;
      const before = child.estimatedHeight;
      const request = {
        ...answer("child", "approval", "Permit?"),
        type: "approval-requested",
        requestId: "approval",
        status: "pending",
      };
      append(request);
      expect(child.estimatedHeight).toBeGreaterThan(before);
      append({ ...request, type: "approval-resolved", status: "accepted" });
      expect(child.transcript().pendingRowIndexes).toHaveLength(0);
      expect(model.cardChanges()).toHaveLength(0);
      dispose();
    }));

  it("holds child output still while keeping its requests live", () =>
    createRoot((dispose) => {
      const { model, append } = fixture(20, 2);
      const view = model.conversation("child")!.conversation!;
      const held = holdTranscript(view.transcript());
      const before = held.entries;
      append(answer("child", "later", "Later output"));
      expect(held.entries).toBe(before);
      const request = {
        ...answer("child", "approval", "Permit?"),
        type: "approval-requested",
        requestId: "approval",
        status: "pending",
      };
      append(request);
      expect(view.transcript().pendingRowIndexes).toHaveLength(1);
      expect(held.entries.some((entry) => entry.text === "Later output")).toBe(false);
      expect(withLivePending(held, view.transcript()).pendingRowIndexes).toHaveLength(1);
      append({ ...request, type: "approval-resolved", status: "accepted" });
      expect(view.transcript().pendingRowIndexes).toHaveLength(0);
      expect(held.entries.some((entry) => entry.text === "Later output")).toBe(false);
      dispose();
    }));
});

it("keeps child opening, text, and authentication activity separate from the main turn", () => {
  createRoot((dispose) => {
    const { model, append } = fixture(0, 0);
    append({ ...answer("child", "done", ""), type: "turn-completed", status: "completed" });
    const side = (type: string): AgentPaneUpdate => ({
      ...marker("btw", null),
      type,
      isPrimaryThread: false,
    });
    append({ ...side("side-conversation-started"), status: "forking" });
    expect(model.interruptible()).toBe(true);
    expect(model.turnActive()).toBe(false);
    append(side("turn-started"));
    expect(model.interruptible()).toBe(true);
    append({ ...side("authentication-requested"), requestId: "auth" });
    expect(model.interruptible()).toBe(true);
    append(side("side-conversation-failed"));
    expect(model.interruptible()).toBe(false);
    dispose();
  });
});
