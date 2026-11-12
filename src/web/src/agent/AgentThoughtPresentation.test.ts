import { createRoot, createSignal } from "solid-js";
import { afterEach, expect, it, vi } from "vitest";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { AgentPaneAccumulator } from "./AgentPaneAccumulator";
import { AgentPaneConversationRows } from "./AgentPaneConversationRows";
import { estimateEntrySize } from "./AgentPaneEstimate";
import * as projection from "./AgentPaneMessages";
import { createAgentPaneModel } from "./AgentPaneModel";
import type { ThoughtDisplay } from "./AgentThoughtPresentation";
import { holdTranscript } from "./AgentTranscriptSnapshot";

vi.mock("solid-js", async () => await vi.importActual("solid-js/dist/solid.js"));
vi.mock("../bridge", () => ({ registerSessionFeature: () => () => {} }));

const disposers: (() => void)[] = [];
afterEach(() => {
  vi.restoreAllMocks();
  for (const dispose of disposers.splice(0)) dispose();
});
const base = { providerId: "acp", threadId: "thread", turnId: "1" };
const start: AgentPaneUpdate = { ...base, type: "turn-started" };
const user = (id: string): AgentPaneUpdate => ({
  ...base,
  type: "user-steer",
  itemId: id,
  text: id,
});
const thought = (id: string, text: string): AgentPaneUpdate => ({
  ...base,
  type: "thought-message-delta",
  itemType: "thought",
  itemId: id,
  text,
});
const tool = (id: string): AgentPaneUpdate => ({
  ...base,
  type: "item-started",
  itemType: "tool",
  itemId: id,
  category: "execute",
  status: "in_progress",
});
const reply = (id: string): AgentPaneUpdate => ({
  ...base,
  type: "item-completed",
  itemType: "agentMessage",
  itemId: id,
  text: id,
});

function fixture() {
  return createRoot((dispose) => {
    disposers.push(dispose);
    const [display, setDisplay] = createSignal<ThoughtDisplay>("current");
    const model = createAgentPaneModel({} as ClientSession, display);
    let flush = () => {};
    const accumulator = new AgentPaneAccumulator((callback) => {
      flush = callback;
    });
    const send = (...updates: AgentPaneUpdate[]) => {
      accumulator.ingestBatch("pane", updates, model.publish);
      flush();
    };
    const load = (updates: AgentPaneUpdate[]) => {
      const rows = new AgentPaneConversationRows(updates);
      updates.forEach((_, index) => {
        rows.register(index);
      });
      model.load(rows);
    };
    return { model, send, load, setDisplay, entries: () => model.transcript().entries };
  });
}

it("updates the current summary without transcript rows and preserves it through tool-only updates", () => {
  const { model, send, entries } = fixture();
  send(start, user("task"), thought("one", "Inspecting"), tool("build"));
  const held = model.transcript();
  expect(held.currentThought).toBe("Inspecting");
  expect(entries().map((entry) => entry.kind)).toEqual(["message", "activity"]);
  send(thought("one", " the build"));
  expect(model.transcript().currentThought).toBe("Inspecting the build");
  expect(held.currentThought).toBe("Inspecting");
  const project = vi.spyOn(projection, "projectAgentTranscript");
  send({ ...tool("build"), type: "item-completed", status: "completed" });
  expect(project).not.toHaveBeenCalled();
  expect(model.transcript().currentThought).toBe("Inspecting the build");
  send(thought("two", "Checking tests"), reply("The build passed"));
  expect(model.transcript().currentThought).toBe("Checking tests");
  expect(entries().find((entry) => entry.text === "The build passed")?.kind).toBe("message");
});

it("clears completed and restarted turns even while old tools remain active", () => {
  const { model, send } = fixture();
  send(start, thought("one", "Old summary"), tool("background"));
  send({ ...base, type: "turn-completed", status: "completed" });
  expect(model.turnActive()).toBe(true);
  expect(model.transcript().currentThought).toBeNull();
  send({ ...start, turnId: "2" });
  expect(model.transcript().currentThought).toBeNull();
  send({ ...thought("one", "New summary"), turnId: "2" });
  expect(model.transcript().currentThought).toBe("New summary");
  send({ ...base, turnId: "2", type: "turn-completed", status: "interrupted" });
  expect(model.transcript().currentThought).toBeNull();
});

it.each<AgentPaneUpdate>([
  user("steer"),
  { ...user("restored-steer"), type: "user-message" },
  { ...user("command"), type: "user-command" },
  { ...base, type: "user-image", itemId: "image", status: "submitted" },
])("clears the current summary on submitted $type without reviving an earlier item", (input) => {
  const { model, send } = fixture();
  send(start, thought("old", "Old summary"), input);
  expect(model.transcript().currentThought).toBeNull();
  send(thought("old", " completed late"));
  expect(model.transcript().currentThought).toBeNull();
  send(thought("new", "New summary"));
  expect(model.transcript().currentThought).toBe("New summary");
});

it("does not clear the summary for unsent attachments or messages from another session", () => {
  const { model, send } = fixture();
  send(
    start,
    thought("one", "Current summary"),
    { ...base, type: "user-image", itemId: "draft", status: "attached" },
    { ...user("peer"), sender: { id: "peer", title: "Other session" } },
  );
  expect(model.transcript().currentThought).toBe("Current summary");
});

it("restores every display choice from history without losing text or disclosure state", () => {
  const { model, load, entries, setDisplay } = fixture();
  load([start, user("task"), thought("one", "First"), thought("two", "Latest"), reply("Answer")]);
  const held = holdTranscript(model.transcript());
  expect(held.currentThought).toBe("Latest");
  setDisplay("all");
  expect(model.transcript().currentThought).toBeNull();
  expect(
    entries()
      .filter((entry) => entry.kind === "thought")
      .map((entry) => entry.text),
  ).toEqual(["First", "Latest"]);
  setDisplay("collapsed");
  const collapsed = entries().filter((entry) => entry.kind === "thought");
  expect(collapsed.every((entry) => entry.thoughtCollapsed)).toBe(true);
  expect(collapsed.map(estimateEntrySize)).toEqual([29, 29]);
  const firstId = collapsed[0]!.id;
  model.setActivityExpanded(firstId, true);
  expect(estimateEntrySize(entries().find((entry) => entry.id === firstId))).toBeGreaterThan(29);
  setDisplay("current");
  expect(model.transcript().currentThought).toBe("Latest");
  expect(entries().some((entry) => entry.kind === "thought")).toBe(false);
  expect(entries().find((entry) => entry.text === "Answer")?.kind).toBe("message");
  setDisplay("collapsed");
  expect(entries().find((entry) => entry.id === firstId)?.thoughtOpen).toBe(true);
  expect(held.currentThought).toBe("Latest");
  expect(held.entries.some((entry) => entry.kind === "thought")).toBe(false);
});

it.each([
  "side-conversation-closed",
  "side-conversation-failed",
])("isolates nested summaries, freezes held views, and clears %s", (type) => {
  const { model, send, setDisplay } = fixture();
  send(
    start,
    thought("root", "Root summary"),
    { ...base, type: "side-conversation-started", conversationId: "side", text: "Side question" },
    { ...start, conversationId: "side" },
    { ...thought("child", "Side summary"), conversationId: "side" },
  );
  const held = holdTranscript(model.transcript());
  const heldSide = held.entries.find((entry) => entry.conversationId === "side")!;
  expect(heldSide.currentThought).toBe("Side summary");
  send({ ...thought("child", " continued"), conversationId: "side" });
  expect(model.conversation("side")?.currentThought).toBe("Side summary continued");
  expect(heldSide.currentThought).toBe("Side summary");
  expect(model.transcript().currentThought).toBe("Root summary");
  setDisplay("all");
  expect(model.conversation("side")?.currentThought).toBeNull();
  expect(model.conversation("side")?.asideEntries?.some((entry) => entry.kind === "thought")).toBe(
    true,
  );
  setDisplay("current");
  expect(model.conversation("side")?.currentThought).toBe("Side summary continued");
  send({ ...base, type, conversationId: "side" });
  expect(model.conversation("side")?.currentThought).toBeNull();
  expect(model.transcript().currentThought).toBe("Root summary");
});
