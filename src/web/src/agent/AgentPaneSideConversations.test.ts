import { describe, expect, it } from "vitest";
import type { AgentPaneUpdate } from "../bridge";
import { toAgentTranscript } from "./AgentPaneMessages";

const primary = {
  providerId: "acp",
  threadId: "primary",
  turnId: "1",
} as const;

function answer(itemId: string, text: string): AgentPaneUpdate {
  return { ...primary, type: "item-completed", itemType: "agentMessage", itemId, text };
}

function aside(conversationId: string): AgentPaneUpdate {
  return {
    providerId: "acp",
    type: "side-conversation-started",
    conversationId,
    anchorTurnId: "1",
    text: conversationId,
  };
}

function order(messages: readonly AgentPaneUpdate[]): string[] {
  return toAgentTranscript(messages).map((entry) => entry.conversationId ?? entry.text ?? entry.id);
}

describe("side conversation placement", () => {
  it("keeps a BTW at its creation position as the same primary turn continues", () => {
    const initial: AgentPaneUpdate[] = [
      { ...primary, type: "user-message", text: "Main question" },
      answer("before", "Before the aside"),
      aside("btw"),
    ];

    expect(order(initial)).toEqual(["Main question", "Before the aside", "btw"]);
    expect(order([...initial, answer("after", "After the aside")])).toEqual([
      "Main question",
      "Before the aside",
      "btw",
      "After the aside",
    ]);
  });

  it("keeps creation order through streaming completion, overlapping asides, and replies", () => {
    const messages: AgentPaneUpdate[] = [
      { ...primary, type: "user-message", text: "Main question" },
      { ...answer("stream", ""), type: "item-started" },
      aside("first aside"),
      { ...answer("stream", "Before the aside"), type: "agent-message-delta" },
      answer("after", "Between the asides"),
      aside("second aside"),
      answer("stream", "Before the aside"),
      answer("tail", "After both asides"),
      {
        ...answer("reply", "Side follow-up"),
        threadId: "first-child",
        conversationId: "first aside",
        anchorTurnId: "1",
      },
    ];

    expect(order(messages)).toEqual([
      "Main question",
      "Before the aside",
      "first aside",
      "Between the asides",
      "second aside",
      "After both asides",
    ]);
    expect(toAgentTranscript(messages)[2]?.asideEntries?.map((entry) => entry.text)).toEqual([
      "first aside",
      "Side follow-up",
    ]);
  });
});
