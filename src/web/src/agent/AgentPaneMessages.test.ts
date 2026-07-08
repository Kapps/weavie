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
      ["Codex", null],
    ]);
    expect(visible[0]?.displaySummary).toBeNull();
    expect(visible[0]?.displayText).toBe("You're on branch `test/codex-4`.");
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

  it("hides successful completed items after replacing their started card", () => {
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

    expect(visible).toHaveLength(0);
  });

  it("keeps failed completed items", () => {
    const visible = toVisibleAgentMessages([
      {
        type: "item-started",
        providerId: "codex",
        itemId: "cmd-1",
        itemType: "commandExecution",
        summary: "git diff --check",
        status: "inProgress",
      },
      {
        type: "item-completed",
        providerId: "codex",
        itemId: "cmd-1",
        itemType: "commandExecution",
        summary: "git diff --check",
        status: "failed",
      },
    ]);

    expect(visible).toHaveLength(1);
    expect(visible[0]?.displayStatus).toBe("failed");
    expect(visible[0]?.displaySummary).toBe("git diff --check");
  });

  it("hides turn diffs and file patch protocol cards", () => {
    const visible = toVisibleAgentMessages([
      { type: "turn-diff", providerId: "codex", text: "diff --git a/file b/file" },
      {
        type: "file-patch-updated",
        providerId: "codex",
        itemId: "patch-1",
        summary: "src/App.cs",
      },
    ]);

    expect(visible).toHaveLength(0);
  });
});
