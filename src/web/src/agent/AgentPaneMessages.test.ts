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
    expect(transcript[0]?.summary).toBe("ran 1 command");
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
    expect(transcript[0]?.summary).toBe("ran 1 command");
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
    expect(transcript[0]?.summary).toBe("command failed: git diff --check");
  });

  it("keeps failed command output with the failed activity", () => {
    const transcript = toAgentTranscript([
      {
        type: "item-completed",
        providerId: "codex",
        itemId: "cmd-1",
        itemType: "commandExecution",
        summary: "git diff --check",
        text: "src/App.cs: trailing whitespace",
        status: "failed",
      },
    ]);

    expect(transcript[0]?.details[0]?.detailText).toBe("src/App.cs: trailing whitespace");
  });

  it("shows a failed turn even when no separate error notification arrived", () => {
    const transcript = toAgentTranscript([
      {
        type: "turn-completed",
        providerId: "codex",
        turnId: "turn-1",
        summary: "Codex usage limit reached",
        text: "You have no weighted tokens left",
        status: "failed",
      },
    ]);

    expect(transcript).toHaveLength(1);
    expect(transcript[0]).toMatchObject({
      kind: "notice",
      tone: "error",
      summary: "Codex usage limit reached",
      text: "You have no weighted tokens left",
      status: "failed",
    });
  });

  it("does not duplicate a failed turn that already emitted an error", () => {
    const transcript = toAgentTranscript([
      {
        type: "error",
        providerId: "codex",
        turnId: "turn-1",
        summary: "Codex usage limit reached",
        text: "You have no weighted tokens left",
        status: "failed",
      },
      {
        type: "turn-completed",
        providerId: "codex",
        turnId: "turn-1",
        summary: "Codex usage limit reached",
        text: "You have no weighted tokens left",
        status: "failed",
      },
    ]);

    expect(transcript).toHaveLength(1);
    expect(transcript[0]?.tone).toBe("error");
    expect(transcript[0]?.status).toBe("failed");
  });

  it("shows when a provider warning will retry", () => {
    const transcript = toAgentTranscript([
      {
        type: "warning",
        providerId: "codex",
        turnId: "turn-1",
        summary: "Codex is temporarily overloaded",
        text: "Retrying the request",
        status: "retrying",
      },
    ]);

    expect(transcript).toHaveLength(1);
    expect(transcript[0]).toMatchObject({
      tone: "warning",
      status: "retrying",
    });
  });

  it("shows only the latest running step in the activity summary", () => {
    const transcript = toAgentTranscript([
      {
        type: "item-started",
        providerId: "codex",
        itemId: "cmd-1",
        itemType: "commandExecution",
        summary: "rg -n agent src/web/src",
        status: "inProgress",
      },
      {
        type: "item-started",
        providerId: "codex",
        itemId: "cmd-2",
        itemType: "commandExecution",
        summary: "pnpm verify",
        status: "inProgress",
      },
    ]);

    expect(transcript).toHaveLength(1);
    expect(transcript[0]?.summary).toBe("running command: pnpm verify");
    expect(transcript[0]?.details.map((step) => step.label)).toEqual([
      "command rg -n agent src/web/src",
      "command pnpm verify",
    ]);
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
    expect(transcript[0]?.summary).toBe("edited 1 file");
    expect(transcript[0]?.details.map((step) => [step.label, step.status])).toEqual([
      ["edit src/App.cs", "updated"],
      ["diff ready", "ready"],
    ]);
    expect(transcript[0]?.details[1]?.detailText).toBe("diff --git a/file b/file");
  });

  it("collapses multiple edit locations into one expandable edit group", () => {
    const transcript = toAgentTranscript([
      {
        type: "edit-location",
        providerId: "codex",
        itemId: "edit-1",
        text: "src/a.ts:1",
      },
      {
        type: "edit-location",
        providerId: "codex",
        itemId: "edit-2",
        text: "src/b.ts:2",
      },
      {
        type: "edit-location",
        providerId: "codex",
        itemId: "edit-3",
        text: "src/c.ts:3",
      },
    ]);

    expect(transcript).toHaveLength(1);
    expect(transcript[0]?.label).toBe("Edits");
    expect(transcript[0]?.summary).toBe("edited 3 files");
    expect(transcript[0]?.details.map((step) => [step.label, step.actionMessage?.itemId])).toEqual([
      ["src/a.ts:1", "edit-1"],
      ["src/b.ts:2", "edit-2"],
      ["src/c.ts:3", "edit-3"],
    ]);
  });

  it("collapses earlier assistant narration into expandable updates", () => {
    const transcript = toAgentTranscript([
      { type: "user-message", providerId: "codex", text: "edit a comment" },
      {
        type: "item-completed",
        providerId: "codex",
        itemId: "msg-1",
        itemType: "agentMessage",
        text: "I'll scan for a low-risk comment.",
        status: "completed",
      },
      {
        type: "item-completed",
        providerId: "codex",
        itemId: "msg-2",
        itemType: "agentMessage",
        text: "I found a safe candidate.",
        status: "completed",
      },
      {
        type: "item-completed",
        providerId: "codex",
        itemId: "msg-3",
        itemType: "agentMessage",
        text: "Edited one comment in src/file.ts.",
        status: "completed",
      },
    ]);

    expect(transcript.map((entry) => [entry.label, entry.text])).toEqual([
      ["You", "edit a comment"],
      ["Earlier updates", null],
      ["Codex", "Edited one comment in src/file.ts."],
    ]);
    expect(transcript[1]?.details.map((step) => step.detailText)).toEqual([
      "I'll scan for a low-risk comment.",
      "I found a safe candidate.",
    ]);
  });

  it("does not collapse final assistant messages across prompts", () => {
    const transcript = toAgentTranscript([
      { type: "user-message", providerId: "codex", text: "first" },
      {
        type: "item-completed",
        providerId: "codex",
        itemId: "msg-1",
        itemType: "agentMessage",
        text: "First result.",
        status: "completed",
      },
      { type: "user-message", providerId: "codex", text: "second" },
      {
        type: "item-completed",
        providerId: "codex",
        itemId: "msg-2",
        itemType: "agentMessage",
        text: "Second result.",
        status: "completed",
      },
    ]);

    expect(transcript.map((entry) => entry.label)).toEqual(["You", "Codex", "You", "Codex"]);
  });

  it("keeps primary and subagent narration with colliding item ids", () => {
    const transcript = toAgentTranscript([
      { type: "user-message", providerId: "codex", threadId: "primary", text: "work" },
      {
        type: "item-completed",
        providerId: "codex",
        threadId: "primary",
        turnId: "turn-1",
        itemId: "message-1",
        itemType: "agentMessage",
        text: "Primary update",
      },
      {
        type: "item-completed",
        providerId: "codex",
        threadId: "subagent",
        turnId: "turn-1",
        itemId: "message-1",
        itemType: "agentMessage",
        text: "Subagent update",
      },
    ]);

    expect(transcript[1]?.details.map((step) => step.detailText)).toEqual(["Primary update"]);
    expect(transcript[2]?.text).toBe("Subagent update");
  });

  it("scopes request resolution and reported errors to their originating thread", () => {
    const transcript = toAgentTranscript([
      {
        type: "approval-requested",
        providerId: "codex",
        threadId: "root",
        turnId: "same-turn",
        itemId: "same-item",
        status: "pending",
      },
      {
        type: "approval-requested",
        providerId: "codex",
        threadId: "sub",
        turnId: "same-turn",
        itemId: "same-item",
        status: "pending",
      },
      {
        type: "approval-resolved",
        providerId: "codex",
        threadId: "sub",
        turnId: "same-turn",
        itemId: "same-item",
        status: "accept",
      },
      {
        type: "error",
        providerId: "codex",
        threadId: "sub",
        turnId: "same-turn",
        text: "Subagent failed",
      },
      {
        type: "turn-completed",
        providerId: "codex",
        threadId: "root",
        turnId: "same-turn",
        status: "failed",
        text: "Root failed",
      },
    ]);

    const requests = transcript.filter((entry) => entry.kind === "request");
    expect(requests.map((entry) => entry.status)).toEqual(["pending", "accepted"]);
    expect(transcript.filter((entry) => entry.tone === "error").map((entry) => entry.text)).toEqual(
      ["Subagent failed", "Root failed"],
    );
  });

  it("clusters the working block just above the result when work precedes later chatter", () => {
    const transcript = toAgentTranscript([
      {
        type: "item-completed",
        providerId: "codex",
        itemId: "cmd-1",
        itemType: "commandExecution",
        summary: "git status",
        status: "completed",
      },
      {
        type: "approval-requested",
        providerId: "codex",
        itemId: "approval-1",
        summary: "Run tests?",
        status: "pending",
      },
      { type: "approval-resolved", providerId: "codex", itemId: "approval-1", status: "accept" },
      {
        type: "item-completed",
        providerId: "codex",
        itemId: "msg-1",
        itemType: "agentMessage",
        text: "Done.",
        status: "completed",
      },
    ]);

    expect(transcript.map((entry) => entry.label)).toEqual(["Permission", "Working", "Codex"]);
  });

  it("keeps the working block at the bottom while streaming after resolved chatter", () => {
    const transcript = toAgentTranscript([
      {
        type: "item-completed",
        providerId: "codex",
        itemId: "cmd-1",
        itemType: "commandExecution",
        summary: "git status",
        status: "completed",
      },
      {
        type: "approval-requested",
        providerId: "codex",
        itemId: "approval-1",
        summary: "Run tests?",
        status: "pending",
      },
      { type: "approval-resolved", providerId: "codex", itemId: "approval-1", status: "accept" },
      {
        type: "item-started",
        providerId: "codex",
        itemId: "cmd-2",
        itemType: "commandExecution",
        summary: "pnpm test",
        status: "inProgress",
      },
    ]);

    expect(transcript.map((entry) => entry.label)).toEqual(["Permission", "Working"]);
  });

  it("keeps a pending request below the working block so the ask stays at the bottom", () => {
    const transcript = toAgentTranscript([
      {
        type: "item-completed",
        providerId: "codex",
        itemId: "cmd-1",
        itemType: "commandExecution",
        summary: "git status",
        status: "completed",
      },
      {
        type: "approval-requested",
        providerId: "codex",
        itemId: "approval-1",
        summary: "Run tests?",
        status: "pending",
      },
    ]);

    expect(transcript.map((entry) => [entry.label, entry.status])).toEqual([
      ["Working", null],
      ["Permission", "pending"],
    ]);
  });

  it("coalesces assistant deltas and replaces them with the completed item", () => {
    const transcript = toAgentTranscript([
      { type: "user-message", providerId: "codex", turnId: "turn-1", text: "hello" },
      {
        type: "agent-message-delta",
        providerId: "codex",
        turnId: "turn-1",
        itemId: "message-1",
        itemType: "agentMessage",
        text: "Hel",
      },
      {
        type: "agent-message-delta",
        providerId: "codex",
        turnId: "turn-1",
        itemId: "message-1",
        itemType: "agentMessage",
        text: "lo",
      },
      {
        type: "item-completed",
        providerId: "codex",
        turnId: "turn-1",
        itemId: "message-1",
        itemType: "agentMessage",
        text: "Hello!",
        status: "completed",
      },
    ]);

    expect(transcript.map((entry) => entry.text)).toEqual(["hello", "Hello!"]);
  });
});
