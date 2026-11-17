import { createRoot, createSignal } from "solid-js";
import { afterEach, expect, it, vi } from "vitest";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { AgentPaneAccumulator } from "./AgentPaneAccumulator";
import { AgentPaneConversationRows } from "./AgentPaneConversationRows";
import { estimateEntrySize } from "./AgentPaneEstimate";
import * as projection from "./AgentPaneMessages";
import { createAgentPaneModel } from "./AgentPaneModel";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import type { ThoughtDisplay } from "./AgentThoughtPresentation";

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
    const [display, setDisplay] = createSignal<ThoughtDisplay>("collapsed");
    const model = createAgentPaneModel({} as ClientSession, display, () => "all");
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

it.each<ThoughtDisplay>([
  "collapsed",
  "all",
])("keeps every thought where it happened with %s thinking", (display) => {
  const { send, entries, setDisplay } = fixture();
  setDisplay(display);
  send(
    { ...user("task"), type: "user-message" },
    tool("build"),
    user("Who is building?"),
    thought("answer", "I am building it."),
    tool("verify"),
    thought("next", "I will verify the build."),
  );
  expect(entries().map((entry) => entry.kind)).toEqual([
    "message",
    "activity",
    "message",
    "thought",
    "activity",
    "thought",
  ]);
  expect(entries().flatMap((entry) => (entry.kind === "thought" ? [entry.text] : []))).toEqual([
    "I am building it.",
    "I will verify the build.",
  ]);
  send(thought("next", " Then I will report."), reply("Done"));
  expect(entries()[5]!.text).toBe("I will verify the build. Then I will report.");
  expect(entries().filter((entry) => entry.kind === "thought")).toHaveLength(2);
});

it("keeps the tools around a thought in one group, live and from history", () => {
  const { model, send, load, entries } = fixture();
  const rows = [start, tool("first"), thought("between", "Checking"), tool("second")];
  send(...rows);
  expect(entries().map((entry) => entry.kind)).toEqual(["activity", "thought"]);
  expect(entries()[0]!.detailCount).toBe(2);
  const project = vi.spyOn(projection, "projectAgentTranscript");
  send({ ...tool("second"), type: "item-completed", status: "completed" }, tool("third"));
  expect(project).not.toHaveBeenCalled();
  expect(entries()[0]!.detailCount).toBe(3);
  expect(model.activityStep("third")!.status).toBe("running");
  load(rows);
  expect(entries().map((entry) => entry.kind)).toEqual(["activity", "thought"]);
  send(reply("Update"), tool("fourth"));
  expect(entries().map((entry) => entry.kind)).toEqual([
    "activity",
    "thought",
    "message",
    "activity",
  ]);
});

it("restores both display choices from history without losing text or disclosure state", () => {
  const { model, load, entries, setDisplay } = fixture();
  load([start, user("task"), thought("one", "First"), thought("two", "Latest"), reply("Answer")]);
  const collapsed = entries().filter((entry) => entry.kind === "thought");
  expect(collapsed.map((entry) => entry.text)).toEqual(["First", "Latest"]);
  expect(collapsed.every((entry) => entry.thoughtCollapsed)).toBe(true);
  expect(collapsed.map(estimateEntrySize)).toEqual([29, 29]);
  const firstId = collapsed[0]!.id;
  model.setActivityExpanded(firstId, true);
  expect(estimateEntrySize(entries().find((entry) => entry.id === firstId))).toBeGreaterThan(29);
  setDisplay("all");
  const shown = entries().filter((entry) => entry.kind === "thought");
  expect(shown.map((entry) => entry.text)).toEqual(["First", "Latest"]);
  expect(shown.some((entry) => entry.thoughtCollapsed)).toBe(false);
  setDisplay("collapsed");
  expect(entries().find((entry) => entry.id === firstId)?.thoughtOpen).toBe(true);
});

it("keeps a side conversation's thoughts in its own transcript", () => {
  const { model, send } = fixture();
  send(
    start,
    thought("root", "Root thought"),
    { ...base, type: "side-conversation-started", conversationId: "side", text: "Side question" },
    { ...start, conversationId: "side" },
    { ...thought("child", "Side thought"), conversationId: "side" },
  );
  const thoughts = (entries: readonly AgentTranscriptEntry[] | undefined) =>
    entries?.flatMap((entry) => (entry.kind === "thought" ? [entry.text] : []));
  expect(thoughts(model.conversation("side")?.asideEntries)).toEqual(["Side thought"]);
  expect(thoughts(model.transcript().entries)).toEqual(["Root thought"]);
});
