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

function subagent(conversationId: string, parent: string | null): AgentPaneUpdate[] {
  const nested = { providerId: "acp", conversationId, anchorTurnId: "1", threadId: conversationId };
  return [
    {
      ...nested,
      type: "subagent-started",
      summary: `name ${conversationId}`,
      text: `task ${conversationId}`,
      parentItemId: parent,
      startedAtMs: 1_000,
      status: "running",
    },
    { ...nested, type: "turn-started", turnId: "1", isPrimaryThread: false },
    { ...nested, type: "item-started", turnId: "1", itemId: "tool:a", itemType: "tool" },
  ];
}

describe("subagent cards", () => {
  it("renders a subagent as its own card at its spawn point, naming a nested spawn's parent", () => {
    const outer = subagent("outer", null);
    const inner = subagent("inner", "outer");
    const entries = toAgentTranscript([
      { ...primary, type: "user-message", text: "Main question" },
      ...outer,
      ...inner,
      answer("after", "After the spawn"),
    ]);

    expect(entries.map((entry) => entry.kind)).toEqual([
      "message",
      "subagent",
      "subagent",
      "message",
    ]);
    expect(entries[1]!.subagent).toEqual({
      completedAtMs: null,
      name: "name outer",
      startedAtMs: 1_000,
      state: "running",
      task: "task outer",
      via: null,
    });
    expect(entries[2]!.subagent?.via).toBe("name outer");
    expect(entries[1]!.streaming).toBe(true);
  });

  it("takes its final state and completion time from the subagent's turn completion", () => {
    const [entry] = toAgentTranscript([
      ...subagent("child", null),
      {
        providerId: "acp",
        conversationId: "child",
        anchorTurnId: "1",
        threadId: "child",
        turnId: "1",
        type: "turn-completed",
        status: "failed",
        completedAtMs: 9_000,
      },
    ]);

    expect(entry!.subagent).toMatchObject({ state: "failed", completedAtMs: 9_000 });
    expect(entry!.tone).toBe("error");
    expect(entry!.streaming).toBe(false);
  });
});
