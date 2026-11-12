import { createRoot, createSignal } from "solid-js";
import { afterEach, expect, it, vi } from "vitest";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { AgentPaneAccumulator } from "./AgentPaneAccumulator";
import { estimateEntrySize } from "./AgentPaneEstimate";
import * as projection from "./AgentPaneMessages";
import { createAgentPaneModel } from "./AgentPaneModel";
import type { ThoughtDisplay } from "./AgentThoughtPresentation";
import { holdTranscript } from "./AgentTranscriptSnapshot";

vi.mock("solid-js", async () => await vi.importActual("solid-js/dist/solid.js"));
vi.mock("../bridge", () => ({ registerSessionFeature: () => () => {} }));

const disposers: (() => void)[] = [];
afterEach(() => {
  for (const dispose of disposers.splice(0)) dispose();
});
const base = { providerId: "acp", threadId: "thread", turnId: "1" };
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
    return { model, send, setDisplay, entries: () => model.transcript().entries };
  });
}

it("folds whole earlier exchanges, preserving current text through tools and completion", () => {
  const { model, send, entries } = fixture();
  send(
    user("task"),
    thought("before", "First"),
    tool("build"),
    thought("before-2", "Second"),
    user("question"),
    thought("answer", "An answer"),
    tool("verify"),
    thought("next", "More work"),
    reply("ordinary reply"),
  );
  const group = entries().find((e) => e.kind === "thought-history")!;
  expect(group.thoughts?.map((e) => e.text)).toEqual(["First", "Second"]);
  expect(
    entries()
      .filter((e) => e.kind === "thought")
      .map((e) => e.text),
  ).toEqual(["An answer", "More work"]);
  expect(entries().filter((e) => e.kind === "activity")).toHaveLength(2);
  send({ ...base, type: "turn-completed", status: "completed" });
  expect(entries().filter((e) => e.kind === "thought")).toHaveLength(2);
  send(user("next question"));
  const histories = entries().filter((e) => e.kind === "thought-history");
  expect(histories).toHaveLength(2);
  expect(histories[1]?.thoughts?.map((e) => e.text)).toEqual(["An answer", "More work"]);
  expect(entries().find((e) => e.text === "ordinary reply")?.kind).toBe("message");
  expect(model.transcript().entryIndexes.size).toBe(entries().length);
});

it("reprojects every display choice while held snapshots and activity ownership stay intact", () => {
  const { model, send, entries, setDisplay } = fixture();
  send(
    { ...base, type: "turn-started" },
    user("task"),
    thought("one", "First"),
    tool("old"),
    user("question"),
    thought("two", "Second"),
  );
  const held = holdTranscript(model.transcript());
  const group = entries().find((e) => e.kind === "thought-history")!;
  const foldedSize = estimateEntrySize(group);
  model.setActivityExpanded(group.id, true);
  expect(estimateEntrySize(entries().find((e) => e.id === group.id))).toBeGreaterThan(foldedSize);
  const opened = entries().find((e) => e.id === group.id);
  const project = vi.spyOn(projection, "projectAgentTranscript");
  send({ ...tool("old"), type: "item-completed", status: "completed" });
  expect(project).not.toHaveBeenCalled();
  expect(entries().find((e) => e.id === group.id)).toBe(opened);
  project.mockRestore();
  setDisplay("all");
  expect(entries().filter((e) => e.kind === "thought-history")).toHaveLength(0);
  expect(entries().filter((e) => e.kind === "thought")).toHaveLength(2);
  setDisplay("collapsed");
  const collapsed = entries().filter((e) => e.kind === "thought");
  expect(collapsed.every((e) => e.thoughtCollapsed)).toBe(true);
  expect(collapsed.map(estimateEntrySize)).toEqual([29, 29]);
  setDisplay("current");
  expect(entries().find((e) => e.id === group.id)?.thoughtOpen).toBe(true);
  expect(held.entries.find((e) => e.id === group.id)?.thoughtOpen).toBe(false);
  expect(held.entries.find((e) => e.text === "Second")?.thoughtCollapsed).toBeUndefined();
});

it("only submitted user input starts an exchange, including restored steering and image-only prompts", () => {
  const { send, entries } = fixture();
  send(
    user("task"),
    thought("one", "First"),
    { ...base, type: "user-image", itemId: "draft-image", status: "attached" },
    { ...user("peer"), sender: { id: "peer", title: "Other session" } },
  );
  expect(entries().some((e) => e.kind === "thought-history")).toBe(false);
  send({ ...user("restored-steer"), type: "user-message" }, thought("two", "Second"));
  expect(entries().filter((e) => e.kind === "thought-history")).toHaveLength(1);
  send({ ...base, type: "user-image", itemId: "submitted-image", status: "submitted" });
  expect(entries().filter((e) => e.kind === "thought-history")).toHaveLength(2);
});

it("keeps parent and side-conversation exchange boundaries independent across live settings", () => {
  const { model, send, entries, setDisplay } = fixture();
  send(
    user("task"),
    thought("one", "Root"),
    { ...base, type: "side-conversation-started", conversationId: "side", text: "Side question" },
    { ...user("side-question"), conversationId: "side" },
    { ...thought("side-thought", "Side reasoning"), conversationId: "side" },
    user("next"),
  );
  expect(entries().filter((e) => e.kind === "thought-history")).toHaveLength(1);
  const side = () => model.conversation("side")!.asideEntries!;
  expect(side().filter((e) => e.kind === "thought")).toHaveLength(1);
  setDisplay("collapsed");
  expect(side().find((e) => e.kind === "thought")?.thoughtCollapsed).toBe(true);
  setDisplay("current");
  expect(side().find((e) => e.kind === "thought")?.thoughtCollapsed).toBeUndefined();
});
