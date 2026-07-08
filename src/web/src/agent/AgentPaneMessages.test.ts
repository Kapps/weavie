import { describe, expect, it } from "vitest";
import type { AgentPaneUpdate } from "../bridge";
import { toVisibleAgentMessages } from "./AgentPaneMessages";

describe("toVisibleAgentMessages", () => {
  it("turns protocol chatter into conversation cards", () => {
    const messages: AgentPaneUpdate[] = [
      { type: "approval-resolved", providerId: "codex", itemId: "approval-1", status: "accept" },
      { type: "approval-resolved", providerId: "codex", itemId: "approval-1", status: "resolved" },
      { type: "status", providerId: "codex", status: "" },
      {
        type: "item-completed",
        providerId: "codex",
        itemId: "cmd-1",
        itemType: "commandExecution",
        summary: "git status --short --branch",
        status: "completed",
      },
      {
        type: "item-completed",
        providerId: "codex",
        itemId: "msg-1",
        itemType: "agentMessage",
        text: "You're on branch `test/codex-4`.",
        status: "completed",
      },
      { type: "turn-completed", providerId: "codex", status: "completed" },
    ];

    const visible = toVisibleAgentMessages(messages);

    expect(visible.map((message) => [message.displayType, message.displayStatus])).toEqual([
      ["Command", "completed"],
      ["Codex", "completed"],
    ]);
    expect(visible[0]?.displaySummary).toBe("git status --short --branch");
    expect(visible[1]?.displaySummary).toBeNull();
    expect(visible[1]?.displayText).toBe("You're on branch `test/codex-4`.");
  });

  it("resolves approval cards in place", () => {
    const visible = toVisibleAgentMessages([
      {
        type: "approval-requested",
        providerId: "codex",
        itemId: "approval-1",
        summary: "Run git status?",
        status: "pending",
      },
      { type: "approval-resolved", providerId: "codex", itemId: "approval-1", status: "accept" },
    ]);

    expect(visible).toHaveLength(1);
    expect(visible[0]?.displayType).toBe("Permission request");
    expect(visible[0]?.displayStatus).toBe("accepted");
  });

  it("replaces started items with their completed item", () => {
    const visible = toVisibleAgentMessages([
      {
        type: "item-started",
        providerId: "codex",
        itemId: "cmd-1",
        itemType: "commandExecution",
        summary: "git status",
        status: "inProgress",
      },
      {
        type: "item-completed",
        providerId: "codex",
        itemId: "cmd-1",
        itemType: "commandExecution",
        summary: "git status",
        status: "completed",
      },
    ]);

    expect(visible).toHaveLength(1);
    expect(visible[0]?.displayStatus).toBe("completed");
  });
});
