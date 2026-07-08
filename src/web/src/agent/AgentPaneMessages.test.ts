import { describe, expect, it } from "vitest";
import type { AgentPaneUpdate } from "../bridge";
import { toAgentTranscript } from "./AgentPaneMessages";

describe("toAgentTranscript", () => {
  it("projects protocol chatter into a dense working transcript", () => {
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

    const transcript = toAgentTranscript(messages);

    expect(transcript.map((entry) => [entry.kind, entry.label, entry.status])).toEqual([
      ["activity", "Working", null],
      ["message", "Codex", null],
    ]);
    expect(transcript[0]?.summary).toBe("1 command");
    expect(transcript[0]?.details).toHaveLength(1);
    expect(transcript[1]?.text).toBe("You're on branch `test/codex-4`.");
  });

  it("resolves approval rows in place", () => {
    const transcript = toAgentTranscript([
      {
        type: "approval-requested",
        providerId: "codex",
        itemId: "approval-1",
        summary: "Run git status?",
        status: "pending",
      },
      { type: "approval-resolved", providerId: "codex", itemId: "approval-1", status: "accept" },
    ]);

    expect(transcript).toHaveLength(1);
    expect(transcript[0]?.kind).toBe("request");
    expect(transcript[0]?.label).toBe("Permission");
    expect(transcript[0]?.status).toBe("accepted");
  });

  it("replaces a started step with the completed state for the same item", () => {
    const transcript = toAgentTranscript([
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

    expect(transcript).toHaveLength(1);
    expect(transcript[0]?.summary).toBe("1 command");
    expect(transcript[0]?.status).toBeNull();
    expect(transcript[0]?.details).toEqual([
      {
        category: "command",
        detailText: null,
        id: "cmd-1",
        label: "command git status",
        status: "completed",
        tone: "muted",
      },
    ]);
  });

  it("keeps failed work visible as an error-toned activity", () => {
    const transcript = toAgentTranscript([
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

    expect(transcript).toHaveLength(1);
    expect(transcript[0]?.tone).toBe("error");
    expect(transcript[0]?.status).toBe("failed");
    expect(transcript[0]?.summary).toBe("command failed");
  });

  it("compacts patch and diff protocol updates into expandable activity", () => {
    const transcript = toAgentTranscript([
      {
        type: "file-patch-updated",
        providerId: "codex",
        itemId: "patch-1",
        summary: "src/App.cs",
      },
      { type: "turn-diff", providerId: "codex", text: "diff --git a/file b/file" },
    ]);

    expect(transcript).toHaveLength(1);
    expect(transcript[0]?.summary).toBe("1 edit");
    expect(transcript[0]?.details.map((step) => [step.label, step.status])).toEqual([
      ["edit src/App.cs", "updated"],
      ["diff ready", "ready"],
    ]);
    expect(transcript[0]?.details[1]?.detailText).toBe("diff --git a/file b/file");
  });
});
