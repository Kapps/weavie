import { describe, expect, it, vi } from "vitest";
import type { AgentPaneUpdate } from "../bridge";
import { ProjectedAgentActivity } from "./AgentPaneActivitySummary";
import { projectAgentTranscript } from "./AgentPaneMessages";

function toAgentTranscript(messages: readonly AgentPaneUpdate[]) {
  const projection = projectAgentTranscript(
    messages.map((message) =>
      message.type.endsWith("-requested") && message.requestId === undefined
        ? { ...message, requestId: message.itemId ?? null }
        : message,
    ),
  );
  for (const entry of projection.entries) {
    const activity = projection.activities.get(entry.id);
    if (activity !== undefined) {
      entry.details = activity.materialize();
    }
  }
  return projection.entries;
}

describe("toAgentTranscript", () => {
  it("renders provider commands as command turns", () => {
    const transcript = toAgentTranscript([
      { type: "user-command", providerId: "acp", turnId: "1", text: "/compact" },
      {
        type: "item-completed",
        providerId: "acp",
        turnId: "1",
        itemType: "agentMessage",
        text: "Done",
      },
    ]);

    expect(transcript[0]).toMatchObject({ label: "Command", text: "/compact", turnStart: true });
  });

  it("summarizes a tool-heavy turn once after all steps are collected", () => {
    const summary = vi.spyOn(ProjectedAgentActivity.prototype, "summary");
    const materialize = vi.spyOn(ProjectedAgentActivity.prototype, "materialize");
    const count = 15_000;
    const turnId = "long-turn";
    const projection = projectAgentTranscript([
      { type: "user-message", providerId: "acp", turnId, text: "Do the long task" },
      ...Array.from<unknown, AgentPaneUpdate>({ length: count }, (_, index) => ({
        type: "item-completed",
        providerId: "acp",
        turnId,
        itemId: `command-${index}`,
        itemType: "commandExecution",
        status: "completed",
        summary: `command ${index}`,
      })),
    ]);

    const transcript = projection.entries;
    expect(summary).toHaveBeenCalledTimes(1);
    expect(materialize).not.toHaveBeenCalled();
    expect(transcript).toHaveLength(2);
    expect(transcript[1]?.summary).toBe(`ran ${count} commands`);
    expect(transcript[1]?.detailCount).toBe(count);
    expect(transcript[1]?.details).toEqual([]);
    expect(projection.activities.get(transcript[1]!.id)?.materialize()).toHaveLength(count);
    summary.mockRestore();
    materialize.mockRestore();
  });

  it("projects protocol chatter into a dense working transcript", () => {
    const messages: AgentPaneUpdate[] = [
      { type: "approval-resolved", providerId: "acp", itemId: "approval-1", status: "accept" },
      { type: "approval-resolved", providerId: "acp", itemId: "approval-1", status: "resolved" },
      { type: "status", providerId: "acp", status: "" },
      {
        type: "item-completed",
        providerId: "acp",
        itemId: "cmd-1",
        itemType: "commandExecution",
        summary: "git status --short --branch",
        status: "completed",
      },
      {
        type: "item-completed",
        providerId: "acp",
        itemId: "msg-1",
        itemType: "agentMessage",
        text: "You're on branch `test/acp-4`.",
        status: "completed",
      },
      { type: "turn-completed", providerId: "acp", status: "completed" },
    ];

    const transcript = toAgentTranscript(messages);

    expect(transcript.map((entry) => [entry.kind, entry.label, entry.status])).toEqual([
      ["activity", "Working", null],
      ["message", "Agent", null],
    ]);
    expect(transcript[0]?.summary).toBe("ran 1 command");
    expect(transcript[0]?.details).toHaveLength(1);
    expect(transcript[1]?.text).toBe("You're on branch `test/acp-4`.");
  });

  it("resolves approval rows in place", () => {
    const transcript = toAgentTranscript([
      {
        type: "approval-requested",
        providerId: "acp",
        itemId: "approval-1",
        summary: "Run git status?",
        status: "pending",
      },
      { type: "approval-resolved", providerId: "acp", itemId: "approval-1", status: "accept" },
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
        providerId: "acp",
        itemId: "cmd-1",
        itemType: "commandExecution",
        summary: "git status",
        status: "inProgress",
      },
      {
        type: "item-completed",
        providerId: "acp",
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
        providerId: "acp",
        itemId: "cmd-1",
        itemType: "commandExecution",
        summary: "git diff --check",
        status: "inProgress",
      },
      {
        type: "item-completed",
        providerId: "acp",
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
        providerId: "acp",
        itemId: "cmd-1",
        itemType: "commandExecution",
        summary: "git diff --check",
        text: "src/App.cs: trailing whitespace",
        status: "failed",
      },
    ]);

    expect(transcript[0]?.details[0]).toMatchObject({
      detailText: "src/App.cs: trailing whitespace",
    });
  });

  it("keeps every tool's output on the step, behind an explicit reveal", () => {
    const transcript = toAgentTranscript([
      {
        type: "item-completed",
        providerId: "acp",
        itemId: "execute-1",
        itemType: "tool",
        category: "execute",
        summary: "run tests",
        text: "test output",
        status: "completed",
      },
      {
        type: "item-completed",
        providerId: "acp",
        itemId: "read-1",
        itemType: "tool",
        category: "read",
        summary: "source file",
        text: "file contents",
        status: "completed",
      },
    ]);

    expect(transcript[0]?.details).toMatchObject([
      { category: "execute", detailText: "test output" },
      { category: "read", detailText: "file contents" },
    ]);
  });

  it("shows a failed turn even when no separate error notification arrived", () => {
    const transcript = toAgentTranscript([
      {
        type: "turn-completed",
        providerId: "acp",
        turnId: "turn-1",
        summary: "ACP usage limit reached",
        text: "You have no weighted tokens left",
        status: "failed",
      },
    ]);

    expect(transcript).toHaveLength(1);
    expect(transcript[0]).toMatchObject({
      kind: "notice",
      tone: "error",
      summary: "ACP usage limit reached",
      text: "You have no weighted tokens left",
      status: "failed",
    });
  });

  it("does not duplicate a failed turn that already emitted an error", () => {
    const transcript = toAgentTranscript([
      {
        type: "error",
        providerId: "acp",
        turnId: "turn-1",
        summary: "ACP usage limit reached",
        text: "You have no weighted tokens left",
        status: "failed",
      },
      {
        type: "turn-completed",
        providerId: "acp",
        turnId: "turn-1",
        summary: "ACP usage limit reached",
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
        providerId: "acp",
        turnId: "turn-1",
        summary: "ACP is temporarily overloaded",
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
        providerId: "acp",
        itemId: "cmd-1",
        itemType: "commandExecution",
        summary: "rg -n agent src/web/src",
        status: "inProgress",
      },
      {
        type: "item-started",
        providerId: "acp",
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
        providerId: "acp",
        itemId: "patch-1",
        summary: "src/App.cs",
      },
      { type: "turn-diff", providerId: "acp", text: "diff --git a/file b/file" },
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
        providerId: "acp",
        itemId: "edit-1",
        text: "src/a.ts:1",
      },
      {
        type: "edit-location",
        providerId: "acp",
        itemId: "edit-2",
        text: "src/b.ts:2",
      },
      {
        type: "edit-location",
        providerId: "acp",
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

  it("keeps every assistant update in its own transcript entry", () => {
    const transcript = toAgentTranscript([
      { type: "user-message", providerId: "acp", text: "edit a comment" },
      {
        type: "item-completed",
        providerId: "acp",
        itemId: "msg-1",
        itemType: "agentMessage",
        text: "I'll scan for a low-risk comment.",
        status: "completed",
      },
      {
        type: "item-completed",
        providerId: "acp",
        itemId: "msg-2",
        itemType: "agentMessage",
        text: "I found a safe candidate.",
        status: "completed",
      },
      {
        type: "item-completed",
        providerId: "acp",
        itemId: "msg-3",
        itemType: "agentMessage",
        text: "Edited one comment in src/file.ts.",
        status: "completed",
      },
    ]);

    expect(transcript.map((entry) => [entry.label, entry.text])).toEqual([
      ["You", "edit a comment"],
      ["Agent", "I'll scan for a low-risk comment."],
      ["Agent", "I found a safe candidate."],
      ["Agent", "Edited one comment in src/file.ts."],
    ]);
    expect(new Set(transcript.slice(1).map((entry) => entry.id)).size).toBe(3);
  });

  it("does not collapse final assistant messages across prompts", () => {
    const transcript = toAgentTranscript([
      { type: "user-message", providerId: "acp", text: "first" },
      {
        type: "item-completed",
        providerId: "acp",
        itemId: "msg-1",
        itemType: "agentMessage",
        text: "First result.",
        status: "completed",
      },
      { type: "user-message", providerId: "acp", text: "second" },
      {
        type: "item-completed",
        providerId: "acp",
        itemId: "msg-2",
        itemType: "agentMessage",
        text: "Second result.",
        status: "completed",
      },
    ]);

    expect(transcript.map((entry) => entry.label)).toEqual(["You", "Agent", "You", "Agent"]);
  });

  it("keeps primary and subagent narration with colliding item ids", () => {
    const transcript = toAgentTranscript([
      { type: "user-message", providerId: "acp", threadId: "primary", text: "work" },
      {
        type: "item-completed",
        providerId: "acp",
        threadId: "primary",
        turnId: "turn-1",
        itemId: "message-1",
        itemType: "agentMessage",
        text: "Primary update",
      },
      {
        type: "item-completed",
        providerId: "acp",
        threadId: "subagent",
        turnId: "turn-1",
        itemId: "message-1",
        itemType: "agentMessage",
        text: "Subagent update",
      },
    ]);

    expect(transcript[1]?.text).toBe("Primary update");
    expect(transcript[2]?.text).toBe("Subagent update");
    expect(transcript[1]?.id).not.toBe(transcript[2]?.id);
  });

  it("scopes request resolution and reported errors to their originating thread", () => {
    const transcript = toAgentTranscript([
      {
        type: "approval-requested",
        providerId: "acp",
        threadId: "root",
        turnId: "same-turn",
        itemId: "same-item",
        status: "pending",
      },
      {
        type: "approval-requested",
        providerId: "acp",
        threadId: "sub",
        turnId: "same-turn",
        itemId: "same-item",
        status: "pending",
      },
      {
        type: "approval-resolved",
        providerId: "acp",
        threadId: "sub",
        turnId: "same-turn",
        itemId: "same-item",
        status: "accept",
      },
      {
        type: "error",
        providerId: "acp",
        threadId: "sub",
        turnId: "same-turn",
        text: "Subagent failed",
      },
      {
        type: "turn-completed",
        providerId: "acp",
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
        providerId: "acp",
        itemId: "cmd-1",
        itemType: "commandExecution",
        summary: "git status",
        status: "completed",
      },
      {
        type: "approval-requested",
        providerId: "acp",
        itemId: "approval-1",
        summary: "Run tests?",
        status: "pending",
      },
      { type: "approval-resolved", providerId: "acp", itemId: "approval-1", status: "accept" },
      {
        type: "item-completed",
        providerId: "acp",
        itemId: "msg-1",
        itemType: "agentMessage",
        text: "Done.",
        status: "completed",
      },
    ]);

    expect(transcript.map((entry) => entry.label)).toEqual(["Permission", "Working", "Agent"]);
  });

  it("keeps the working block at the bottom while streaming after resolved chatter", () => {
    const transcript = toAgentTranscript([
      {
        type: "item-completed",
        providerId: "acp",
        itemId: "cmd-1",
        itemType: "commandExecution",
        summary: "git status",
        status: "completed",
      },
      {
        type: "approval-requested",
        providerId: "acp",
        itemId: "approval-1",
        summary: "Run tests?",
        status: "pending",
      },
      { type: "approval-resolved", providerId: "acp", itemId: "approval-1", status: "accept" },
      {
        type: "item-started",
        providerId: "acp",
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
        providerId: "acp",
        itemId: "cmd-1",
        itemType: "commandExecution",
        summary: "git status",
        status: "completed",
      },
      {
        type: "approval-requested",
        providerId: "acp",
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
    const streamed: AgentPaneUpdate[] = [
      { type: "user-message", providerId: "acp", turnId: "turn-1", text: "hello" },
      {
        type: "agent-message-delta",
        providerId: "acp",
        turnId: "turn-1",
        itemId: "message-1",
        itemType: "agentMessage",
        text: "Hel",
      },
      {
        type: "agent-message-delta",
        providerId: "acp",
        turnId: "turn-1",
        itemId: "message-1",
        itemType: "agentMessage",
        text: "lo",
      },
    ];
    const provisional = toAgentTranscript(streamed);

    expect(provisional.map((entry) => [entry.text, entry.streaming])).toEqual([
      ["hello", false],
      ["Hello", true],
    ]);

    const completed = toAgentTranscript([
      ...streamed,
      {
        type: "item-completed",
        providerId: "acp",
        turnId: "turn-1",
        itemId: "message-1",
        itemType: "agentMessage",
        text: "Hello!",
        status: "completed",
      },
    ]);

    expect(completed.map((entry) => [entry.text, entry.streaming])).toEqual([
      ["hello", false],
      ["Hello!", false],
    ]);
  });

  it("coalesces thought deltas into one live activity", () => {
    const transcript = toAgentTranscript([
      {
        type: "thought-message-delta",
        providerId: "acp",
        turnId: "turn-1",
        itemId: "thought-1",
        itemType: "thought",
        text: "Inspect",
      },
      {
        type: "thought-message-delta",
        providerId: "acp",
        turnId: "turn-1",
        itemId: "thought-1",
        itemType: "thought",
        text: " code",
      },
    ]);

    expect(transcript).toHaveLength(1);
    expect(transcript[0]?.details).toMatchObject([{ detailText: "Inspect code" }]);
  });

  it("promotes a completed plan into an openable result while its delta stays provisional activity", () => {
    const provisional = toAgentTranscript([
      {
        type: "plan-delta",
        providerId: "acp",
        threadId: "thread-1",
        turnId: "turn-1",
        itemId: "plan-1",
        itemType: "plan",
        text: "# Draft plan",
      },
    ]);
    expect(provisional).toMatchObject([{ kind: "activity", label: "Working" }]);

    const completed = toAgentTranscript([
      {
        type: "plan-delta",
        providerId: "acp",
        threadId: "thread-1",
        turnId: "turn-1",
        itemId: "plan-1",
        itemType: "plan",
        text: "# Draft plan",
      },
      {
        type: "item-completed",
        providerId: "acp",
        threadId: "thread-1",
        turnId: "turn-1",
        itemId: "plan-1",
        itemType: "plan",
        text: "# Final plan",
        status: "completed",
      },
    ]);

    expect(completed).toHaveLength(1);
    expect(completed[0]).toMatchObject({
      kind: "plan",
      label: "Plan",
      summary: "Ready to review in the editor",
      text: null,
      tone: "assistant",
    });
    expect(completed[0]?.actionMessage).toMatchObject({
      itemId: "plan-1",
      threadId: "thread-1",
      turnId: "turn-1",
    });

    const unavailable = toAgentTranscript([
      {
        type: "item-completed",
        providerId: "acp",
        threadId: "thread-1",
        turnId: "turn-1",
        itemId: "plan-2",
        itemType: "plan",
        text: " ",
      },
    ]);
    expect(unavailable[0]).toMatchObject({ summary: "Plan is unavailable", actionMessage: null });
  });

  it("keeps an ACP task-list update in activity instead of promoting it to a plan", () => {
    const transcript = toAgentTranscript([
      {
        type: "item-completed",
        providerId: "acp",
        threadId: "thread-1",
        turnId: "turn-1",
        itemId: "progress:current",
        itemType: "progress",
        category: "progress",
        summary: "Task list",
        text: "- [x] Inspect\n- [~] Implement",
        status: "updated",
      },
    ]);

    expect(transcript).toHaveLength(1);
    expect(transcript[0]).toMatchObject({ kind: "activity", actionMessage: null });
    expect(transcript[0]?.details).toMatchObject([
      { category: "progress", detailText: "- [x] Inspect\n- [~] Implement" },
    ]);
  });

  it("assigns a unique id to every entry and nested step (the reconcile key precondition)", () => {
    const transcript = toAgentTranscript([
      { type: "user-message", providerId: "acp", turnId: "t1", itemId: "u1", text: "q1" },
      {
        type: "item-completed",
        providerId: "acp",
        turnId: "t1",
        itemId: "cmd1",
        itemType: "commandExecution",
        summary: "git status",
        status: "completed",
      },
      {
        type: "item-completed",
        providerId: "acp",
        turnId: "t1",
        itemId: "cmd2",
        itemType: "commandExecution",
        summary: "npm test",
        status: "completed",
      },
      {
        type: "item-completed",
        providerId: "acp",
        turnId: "t1",
        itemId: "a1",
        itemType: "agentMessage",
        text: "answer one",
        status: "completed",
      },
      { type: "turn-completed", providerId: "acp", turnId: "t1", status: "completed" },
      // A message with no itemId must still get a unique id, not collide on "".
      { type: "warning", providerId: "acp", turnId: "t1", summary: "heads up" },
      { type: "user-message", providerId: "acp", turnId: "t2", itemId: "u2", text: "q2" },
      {
        type: "item-started",
        providerId: "acp",
        turnId: "t2",
        itemId: "cmd3",
        itemType: "commandExecution",
        summary: "ls",
        status: "inProgress",
      },
    ]);

    const ids = [
      ...transcript.map((entry) => entry.id),
      ...transcript.flatMap((entry) => entry.details.map((step) => step.id)),
    ];
    expect(ids).not.toContain("");
    expect(new Set(ids).size).toBe(ids.length);
  });

  it("keeps a durable entry's id stable as later messages arrive", () => {
    const prefix: AgentPaneUpdate[] = [
      { type: "user-message", providerId: "acp", turnId: "t1", itemId: "u1", text: "q1" },
      {
        type: "item-completed",
        providerId: "acp",
        turnId: "t1",
        itemId: "a1",
        itemType: "agentMessage",
        text: "answer one",
        status: "completed",
      },
    ];
    const before = toAgentTranscript(prefix);
    const assistantId = before.find((entry) => entry.tone === "assistant")?.id;
    expect(assistantId).toBeDefined();

    const after = toAgentTranscript([
      ...prefix,
      { type: "user-message", providerId: "acp", turnId: "t2", itemId: "u2", text: "q2" },
    ]);
    expect(after.find((entry) => entry.id === assistantId)?.text).toBe("answer one");
  });
});
