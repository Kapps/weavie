import { createRoot, createSignal } from "solid-js";
import { expect, it, vi } from "vitest";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { AgentPaneAccumulator } from "./AgentPaneAccumulator";
import * as projection from "./AgentPaneMessages";
import { createAgentPaneModel } from "./AgentPaneModel";
import { subagentCardEntries } from "./AgentPaneSideConversations";
import type { ProgressDisplay } from "./AgentUpdatePresentation";

vi.mock("solid-js", async () => await vi.importActual("solid-js/dist/solid.js"));
vi.mock("../bridge", () => ({ registerSessionFeature: () => () => {} }));

const base = { providerId: "acp", threadId: "main", turnId: "turn" };
const reply = (id: string, text: string): AgentPaneUpdate => ({
  ...base,
  type: "item-completed",
  itemType: "agentMessage",
  itemId: id,
  text,
});
const tool = (id: string, status: string): AgentPaneUpdate => ({
  ...base,
  type: "item-completed",
  itemType: "tool",
  itemId: id,
  category: "execute",
  status,
  text: `output of ${id}`,
  summary: id,
});
function fixture() {
  const [display, setDisplay] = createSignal<ProgressDisplay>("latest");
  const model = createAgentPaneModel({} as ClientSession, () => "all", display);
  let flush = () => {};
  const accumulator = new AgentPaneAccumulator((callback) => {
    flush = callback;
  });
  const send = (...messages: AgentPaneUpdate[]) => {
    accumulator.ingestBatch("pane", messages, model.publish);
    flush();
  };
  const entries = () => model.transcript().entries;
  return { model, send, entries, setDisplay };
}

it("folds narration and completed work while retaining the latest reply and pending work", () =>
  createRoot((dispose) => {
    const { model, send, entries, setDisplay } = fixture();
    send(
      { ...base, type: "user-message", text: "Build this" },
      { ...base, type: "turn-started" },
      reply("first", "Inspecting"),
      tool("read", "completed"),
      reply("second", "Building"),
      tool("build", "failed"),
      {
        ...base,
        type: "approval-requested",
        itemId: "permission",
        requestId: "permission",
        status: "pending",
      },
      reply("latest", "Checking"),
      tool("check", "inProgress"),
    );
    expect(
      entries()
        .filter((entry) => entry.tone === "assistant")
        .map((entry) => entry.text),
    ).toEqual(["Checking"]);
    const group = entries().find((entry) => entry.kind === "updates")!;
    expect(group.detailCount).toBe(4);
    expect(
      entries()
        .filter((entry) => entry.kind === "activity")
        .map((entry) => entry.status),
    ).toEqual(["running"]);
    expect(entries()[model.transcript().pendingRowIndexes[0]!]!.label).toBe("Permission");
    model.setActivityExpanded(group.id, true);
    expect(
      entries()
        .filter((entry) => entry.tone === "assistant")
        .map((entry) => entry.text),
    ).toEqual(["Inspecting", "Building", "Checking"]);
    model.setActivityExpanded(group.id, false);
    setDisplay("all");
    expect(entries().some((entry) => entry.kind === "updates")).toBe(false);
    expect(entries().filter((entry) => entry.tone === "assistant")).toHaveLength(3);
    setDisplay("latest");
    expect(entries().find((entry) => entry.kind === "updates")!.id).toBe(group.id);
    send(
      {
        ...base,
        type: "approval-resolved",
        itemId: "permission",
        requestId: "permission",
        status: "accepted",
      },
      tool("check", "completed"),
      reply("final", "Done"),
      { ...base, type: "turn-completed", status: "completed" },
    );
    const final = model.transcript();
    expect(final.entries[final.latestResultIndex!]!.text).toBe("Done");
    expect(final.sectionLabels.get(final.entries[final.latestResultIndex!]!.id)).toBe("Results");
    expect(final.entries.find((entry) => entry.kind === "updates")!.detailCount).toBe(7);
    dispose();
  }));

it("updates hidden activity through canonical indexes and restores its output on expansion", () =>
  createRoot((dispose) => {
    const { model, send, entries } = fixture();
    send(reply("first", "Inspecting"), tool("read", "completed"), reply("latest", "Checking"));
    const group = entries().find((entry) => entry.kind === "updates")!;
    const project = vi.spyOn(projection, "projectAgentTranscript");
    send(tool("read", "failed"));
    expect(project).not.toHaveBeenCalled();
    expect(entries().find((entry) => entry.kind === "activity")).toBeUndefined();
    expect(entries().filter((entry) => entry.kind === "activity")).toHaveLength(0);
    model.setActivityExpanded(group.id, true);
    const activity = entries().find((entry) => entry.kind === "activity")!;
    const reader = activity.activityDetails!.retain();
    expect(reader.steps()[0]!.detailText).toBe("output of read");
    expect(reader.steps()[0]!.status).toBe("failed");
    send(reply("final", "Finished"));
    expect(entries().find((entry) => entry.id === group.id)!.updatesOpen).toBe(true);
    project.mockRestore();
    dispose();
  }));

it("keeps replies to steering visible and isolates nested conversations", () =>
  createRoot((dispose) => {
    const { model, send, entries } = fixture();
    send(
      reply("before", "Initial update"),
      reply("before-latest", "Before steering"),
      { ...base, type: "user-steer", text: "Who is building?" },
      reply("answer", "I am"),
      {
        providerId: "acp",
        type: "side-conversation-started",
        conversationId: "btw",
        text: "Side question",
      },
      { ...reply("child-first", "Side update"), conversationId: "btw" },
      { ...reply("child-latest", "Side answer"), conversationId: "btw" },
    );
    expect(
      entries()
        .filter((entry) => entry.tone === "assistant" && entry.kind === "message")
        .map((entry) => entry.text),
    ).toEqual(["Before steering", "I am"]);
    const child = model.conversation("btw")!;
    expect(
      child
        .asideEntries!.filter((entry) => entry.kind === "message" && entry.tone === "assistant")
        .map((entry) => entry.text),
    ).toEqual(["Side answer"]);
    const group = child.asideEntries!.find((entry) => entry.kind === "updates")!;
    model.setActivityExpanded(group.id, true);
    expect(
      child
        .asideEntries!.filter((entry) => entry.kind === "message" && entry.tone === "assistant")
        .map((entry) => entry.text),
    ).toEqual(["Side update", "Side answer"]);
    expect(entries().filter((entry) => entry.kind === "updates")[0]!.updatesOpen).toBe(false);
    dispose();
  }));

it("never folds final replies across turn identities when no prompt row was reported", () =>
  createRoot((dispose) => {
    const { send, entries } = fixture();
    send(
      reply("first-final", "First result"),
      { ...reply("second-update", "Second update"), turnId: "second" },
      { ...reply("second-final", "Second result"), turnId: "second" },
    );
    expect(
      entries()
        .filter((entry) => entry.kind === "message")
        .map((entry) => entry.text),
    ).toEqual(["First result", "Second result"]);
    expect(entries().find((entry) => entry.kind === "updates")!.detailCount).toBe(1);
    dispose();
  }));

it("folds tool-only and trailing work without hiding the sole reply or changing the group identity", () =>
  createRoot((dispose) => {
    const { model, send, entries } = fixture();
    send(tool("read", "completed"));
    const group = entries()[0]!;
    expect(group.kind).toBe("updates");
    expect(group.detailCount).toBe(1);
    model.setActivityExpanded(group.id, true);
    send(reply("only", "Ready"), tool("check", "failed"));
    expect(entries()[0]!.id).toBe(group.id);
    expect(entries()[0]!.updatesOpen).toBe(true);
    model.setActivityExpanded(group.id, false);
    expect(entries().map((entry) => entry.kind)).toEqual(["updates", "message"]);
    expect(entries()[model.transcript().latestResultIndex!]!.text).toBe("Ready");
    dispose();
  }));

it("puts a trailing-work disclosure before the only reply", () =>
  createRoot((dispose) => {
    const { model, send, entries } = fixture();
    send(reply("only", "Ready"), tool("check", "failed"));
    expect(entries().map((entry) => entry.kind)).toEqual(["updates", "message"]);
    expect(entries()[model.transcript().latestResultIndex!]!.text).toBe("Ready");
    dispose();
  }));

it("folds completed subagents while preserving live descendants and pending requests", () =>
  createRoot((dispose) => {
    const { model, send, entries } = fixture();
    const child = (conversationId: string, type: string): AgentPaneUpdate => ({
      ...base,
      conversationId,
      threadId: conversationId,
      type,
    });
    send(
      { providerId: "acp", type: "side-conversation-started", conversationId: "btw" },
      { ...child("worker", "subagent-started"), parentItemId: "btw" },
      { ...child("nested", "subagent-started"), parentItemId: "worker" },
      { ...child("worker", "turn-completed"), status: "completed" },
      reply("final", "Done"),
    );
    const parent = model.conversation("btw")!.conversation!;
    expect(parent.transcript().entries.some((entry) => entry.id === "subagent-nested")).toBe(true);
    const expandedHeight = parent.estimatedHeight;
    send({ ...child("nested", "turn-completed"), status: "completed" });
    expect(parent.transcript().entries.map((entry) => entry.kind)).toEqual(["updates"]);
    expect(parent.estimatedHeight).toBeLessThan(expandedHeight);
    const foldedHeight = parent.estimatedHeight;
    const request = {
      ...child("nested", "input-requested"),
      itemId: "question",
      requestId: "question",
      status: "pending",
    };
    send(request);
    expect(parent.transcript().entries.some((entry) => entry.kind === "subagent")).toBe(true);
    expect(parent.estimatedHeight).toBeGreaterThan(foldedHeight);
    expect(entries()[model.transcript().pendingRowIndexes[0]!]!.id).toBe("aside-btw");
    send({ ...request, type: "input-resolved", status: "accepted" });
    expect(parent.transcript().entries.map((entry) => entry.kind)).toEqual(["updates"]);
    expect(parent.estimatedHeight).toBe(foldedHeight);
    expect(model.transcript().pendingRowIndexes).toHaveLength(0);
    dispose();
  }));

it("keeps all explicitly opened subagent history and its collapse control in the card", () =>
  createRoot((dispose) => {
    const { model, send } = fixture();
    send(
      { ...base, type: "subagent-started", conversationId: "child" },
      ...Array.from({ length: 8 }, (_, index) => ({
        ...reply(`child-${index}`, `Update ${index}`),
        conversationId: "child",
      })),
      { ...base, conversationId: "child", type: "turn-completed", status: "completed" },
    );
    const child = model.conversation("child")!;
    model.setActivityExpanded(child.id, true);
    const preview = () => subagentCardEntries(child.asideEntries!, true);
    const group = preview()[0]!;
    const height = child.conversation!.estimatedHeight;
    expect(preview().map((entry) => entry.kind)).toEqual(["updates", "message"]);
    model.setActivityExpanded(group.id, true);
    expect(preview()).toHaveLength(9);
    expect(preview()[0]!.updatesOpen).toBe(true);
    expect(preview()[1]!.text).toBe("Update 0");
    expect(child.conversation!.estimatedHeight).toBeGreaterThan(height);
    expect(subagentCardEntries(child.asideEntries!, false)).toEqual([]);
    model.setActivityExpanded(group.id, false);
    expect(preview().map((entry) => entry.kind)).toEqual(["updates", "message"]);
    expect(child.conversation!.estimatedHeight).toBe(height);
    dispose();
  }));
