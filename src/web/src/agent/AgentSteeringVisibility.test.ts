import { createRoot } from "solid-js";
import { expect, it, vi } from "vitest";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { AgentPaneAccumulator } from "./AgentPaneAccumulator";
import * as projection from "./AgentPaneMessages";
import { createAgentPaneModel } from "./AgentPaneModel";

vi.mock("solid-js", async () => await vi.importActual("solid-js/dist/solid.js"));
vi.mock("../bridge", () => ({ registerSessionFeature: () => () => {} }));

it("keeps Thinking after its steer and owns late tool updates in the original segment", () =>
  createRoot((dispose) => {
    const model = createAgentPaneModel(
      {} as ClientSession,
      () => "all",
      () => "all",
    );
    let flush = () => {};
    const accumulator = new AgentPaneAccumulator((callback) => {
      flush = callback;
    });
    const send = (...messages: AgentPaneUpdate[]) => {
      accumulator.ingestBatch("pane", messages, model.publish);
      flush();
    };
    const base = { providerId: "acp", threadId: "thread", turnId: "turn" };
    const tool = (id: string): AgentPaneUpdate => ({
      ...base,
      type: "item-started",
      itemType: "tool",
      itemId: id,
      category: "execute",
      status: "in_progress",
    });
    send(
      { ...base, type: "user-message", text: "Build this" },
      tool("old"),
      { ...base, type: "user-steer", text: "Who is building?" },
      {
        ...base,
        type: "thought-message-delta",
        itemType: "thought",
        itemId: "reply",
        text: "I am ",
      },
      {
        ...base,
        type: "thought-message-delta",
        itemType: "thought",
        itemId: "reply",
        text: "building it.",
      },
      tool("new"),
    );
    const entries = model.transcript().entries;
    expect(entries.map((entry) => entry.label)).toEqual([
      "You",
      "Working",
      "Steer",
      "Thinking",
      "Working",
    ]);
    expect(entries[3]).toMatchObject({ text: "I am building it.", streaming: true, details: [] });
    expect(model.transcript().latestResultIndex).toBeNull();
    const oldId = entries[1]!.id;
    const newId = entries[4]!.id;
    const project = vi.spyOn(projection, "projectAgentTranscript");
    send({ ...tool("old"), type: "item-completed", status: "completed" }, tool("next"));
    expect(project).not.toHaveBeenCalled();
    expect(model.transcript().entries.map((entry) => entry.id)).toEqual(
      entries.map((entry) => entry.id),
    );
    model.setActivityExpanded(oldId, true);
    model.setActivityExpanded(newId, true);
    expect(model.transcript().entries[1]!.details).toMatchObject([
      { id: "old", status: "completed" },
    ]);
    expect(model.transcript().entries[4]!.details.map((step) => step.id)).toEqual(["new", "next"]);
    project.mockRestore();
    send(
      { ...base, type: "user-steer", text: "What happens next?" },
      {
        ...base,
        type: "thought-message-delta",
        itemType: "thought",
        itemId: "second-reply",
        text: "I will verify the build.",
      },
      tool("verification"),
    );
    expect(model.transcript().entries.map((entry) => entry.label)).toEqual([
      "You",
      "Working",
      "Steer",
      "Thinking",
      "Working",
      "Steer",
      "Thinking",
      "Working",
    ]);
    expect(
      model
        .transcript()
        .entries.slice(0, 5)
        .map((entry) => entry.id),
    ).toEqual(entries.map((entry) => entry.id));
    expect(model.transcript().entries[6]!.text).toBe("I will verify the build.");
    send({
      ...base,
      type: "item-completed",
      itemType: "agentMessage",
      itemId: "final",
      text: "Done",
    });
    expect(model.transcript().entries[model.transcript().latestResultIndex!]!.text).toBe("Done");
    dispose();
  }));
