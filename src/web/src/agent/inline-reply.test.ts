import { createRoot } from "solid-js";
import { describe, expect, it } from "vitest";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { createTestAgentPaneModel } from "./AgentPaneTestModel";
import { submissionReply } from "./inline-reply";

const row = (type: string, turnId: string, itemId: string, text: string): AgentPaneUpdate => ({
  type,
  providerId: "acp",
  threadId: "thread-1",
  turnId,
  itemId,
  text,
});
const said = (turnId: string, itemId: string, text: string): AgentPaneUpdate => ({
  ...row("item-completed", turnId, itemId, text),
  itemType: "agentMessage",
});
const turn = (type: "turn-started" | "turn-completed", turnId: string): AgentPaneUpdate => ({
  type,
  providerId: "acp",
  threadId: "thread-1",
  turnId,
});

function reply(messages: AgentPaneUpdate[], id: string) {
  return createRoot((dispose) => {
    const model = createTestAgentPaneModel({} as ClientSession);
    model.replace(messages);
    const result = submissionReply(model.transcript(), id);
    dispose();
    return result && { said: result.entries.map((entry) => entry.text), working: result.working };
  });
}

describe("the reply to one submission", () => {
  const asked = [
    turn("turn-started", "1"),
    row("user-message", "1", "ask", "why does this retry twice?"),
    { ...row("user-selection", "1", "excerpt", "retry()"), summary: "notes.txt:1:1-1:8" },
  ];

  it("is absent until the prompt reaches the transcript", () => {
    expect(reply([row("user-message", "1", "other", "hello")], "ask")).toBeNull();
  });

  it("holds what the agent says after the prompt and its attachments while the turn runs", () => {
    expect(reply(asked, "ask")).toEqual({ said: [], working: true });
    expect(
      reply([...asked, said("1", "a", "Looking."), said("1", "b", "It backs off.")], "ask"),
    ).toEqual({
      said: ["Looking.", "It backs off."],
      working: true,
    });
  });

  it("settles when the turn ends, and carries the turn's failure", () => {
    expect(
      reply([...asked, said("1", "a", "It backs off."), turn("turn-completed", "1")], "ask"),
    ).toEqual({
      said: ["It backs off."],
      working: false,
    });
    const failed = {
      ...row("error", "1", "boom", "The agent stopped responding."),
      status: "failed",
    };
    expect(reply([...asked, failed, turn("turn-completed", "1")], "ask")?.said).toEqual([
      "The agent stopped responding.",
    ]);
  });

  it("ends at the user's next input, whose answer belongs to that input", () => {
    const next = [
      ...asked,
      said("1", "a", "It backs off."),
      turn("turn-completed", "1"),
      turn("turn-started", "2"),
      row("user-message", "2", "later", "and this?"),
      said("2", "c", "That one is unrelated."),
    ];
    expect(reply(next, "ask")).toEqual({ said: ["It backs off."], working: false });
    expect(reply(next, "later")).toEqual({ said: ["That one is unrelated."], working: true });
  });

  it("follows a steer sent into a running turn", () => {
    const steered = [
      turn("turn-started", "1"),
      row("user-message", "1", "first", "refactor this"),
      said("1", "a", "Starting."),
      row("user-steer", "1", "ask", "why does this retry twice?"),
      said("1", "b", "It backs off."),
    ];
    expect(reply(steered, "ask")).toEqual({ said: ["It backs off."], working: true });
    expect(reply(steered, "first")).toEqual({ said: ["Starting."], working: false });
  });

  it("leaves out a failed aside, and an attachment-only prompt that starts the next turn", () => {
    const aside = (type: string): AgentPaneUpdate => ({
      type,
      providerId: "acp",
      conversationId: "btw-1",
      anchorTurnId: "1",
      text: "an unrelated question",
      summary: type === "side-conversation-failed" ? "The fork failed." : "BTW",
    });
    const failedAside = [
      ...asked,
      said("1", "a", "It backs off."),
      turn("turn-completed", "1"),
      aside("side-conversation-started"),
      aside("side-conversation-failed"),
    ];
    expect(reply(failedAside, "ask")?.said).toEqual(["It backs off."]);
    const imageOnly = [
      ...asked,
      turn("turn-completed", "1"),
      turn("turn-started", "2"),
      row("user-image", "2", "picture", "/tmp/picture.png"),
      said("2", "c", "That is a diagram."),
    ];
    expect(reply(imageOnly, "ask")).toEqual({ said: [], working: false });
  });
});
