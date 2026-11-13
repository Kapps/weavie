import { createRoot, createSignal } from "solid-js";
import { expect, it, vi } from "vitest";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { AgentPaneAccumulator } from "./AgentPaneAccumulator";
import * as projection from "./AgentPaneMessages";
import { createAgentPaneModel } from "./AgentPaneModel";
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

it("folds narration and completed work while retaining the latest reply, requests, and failures", () =>
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
    expect(group.detailCount).toBe(2);
    expect(
      entries()
        .filter((entry) => entry.kind === "activity")
        .map((entry) => entry.status),
    ).toEqual(["failed", "running"]);
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
    expect(final.entries.find((entry) => entry.kind === "updates")!.detailCount).toBe(3);
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
    expect(entries().find((entry) => entry.kind === "activity")!.status).toBe("failed");
    send(tool("read", "completed"));
    expect(entries().filter((entry) => entry.kind === "activity")).toHaveLength(0);
    model.setActivityExpanded(group.id, true);
    const activity = entries().find((entry) => entry.kind === "activity")!;
    model.setActivityExpanded(activity.id, true);
    expect(entries().find((entry) => entry.id === activity.id)!.details[0]!.detailText).toBe(
      "output of read",
    );
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
