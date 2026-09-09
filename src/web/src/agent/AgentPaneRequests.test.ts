import { createRoot } from "solid-js";
import { describe, expect, it, vi } from "vitest";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { paneItemIdentity } from "./AgentPaneIdentity";
import { createAgentPaneModel } from "./AgentPaneModel";

vi.mock("../bridge", () => ({ registerSessionFeature: () => () => {} }));

describe("pending requests in the agent transcript", () => {
  it("keeps every request type after subsequent output until its own resolution", () => {
    createRoot((dispose) => {
      const model = createAgentPaneModel({} as ClientSession);
      const input = request("input", "input-1");
      const approval = request("approval", "approval-1");
      const authentication = request("authentication", "authentication-1");
      const updates = [output("first"), input, output("second"), approval, authentication];
      model.replace(updates);
      expect(model.entries.map((entry) => entry.id)).toEqual(
        [updates[0]!, updates[2]!, input, approval, authentication].map(paneItemIdentity),
      );
      expect(model.pendingRowIndexes()).toEqual([2, 3, 4]);
      expect(model.keyboardRequestKey()).toBe(paneItemIdentity(authentication));

      const later = output("third");
      updates.push(later);
      model.publish(updates, [later]);
      expect(model.entries.map((entry) => entry.id)).toEqual(
        [updates[0]!, updates[2]!, later, input, approval, authentication].map(paneItemIdentity),
      );

      const resolved = { ...input, type: "input-resolved", status: "accepted" };
      updates.push(resolved);
      model.publish(updates, [resolved]);
      expect(model.entries[1]?.status).toBe("accepted");
      expect(model.entries.map((entry) => entry.id)).toEqual(
        [updates[0]!, input, updates[2]!, later, approval, authentication].map(paneItemIdentity),
      );
      expect(model.pendingRowIndexes()).toEqual([4, 5]);
      expect(model.keyboardRequestKey()).toBe(paneItemIdentity(authentication));
      dispose();
    });
  });

  it("retains aside owners while ordering their requests within their own conversation", () => {
    createRoot((dispose) => {
      const model = createAgentPaneModel({} as ClientSession);
      const question = {
        ...request("input", "side-input"),
        conversationId: "side-1",
        threadId: "side-thread",
      };
      const sideOutput = {
        ...output("side-output"),
        conversationId: question.conversationId,
        threadId: question.threadId,
      };
      const updates: AgentPaneUpdate[] = [
        output("primary"),
        {
          type: "side-conversation-started",
          providerId: "acp",
          conversationId: "side-1",
          anchorTurnId: "turn",
          text: "Side question",
        },
        question,
        sideOutput,
        output("later-primary"),
      ];
      model.replace(updates);
      const index = model.entries.findIndex((entry) => entry.kind === "aside");
      expect(index).toBeGreaterThanOrEqual(0);
      expect(model.entries.filter((entry) => entry.kind === "request")).toEqual([]);
      expect(model.entries[index]?.asideEntries?.at(-1)?.id).toBe(paneItemIdentity(question));
      expect(model.pendingRowIndexes()).toEqual([index]);
      expect(model.keyboardRequestKey()).toBe(paneItemIdentity(question));

      const resolved = { ...question, type: "input-resolved", status: "accepted" };
      model.publish([...updates, resolved], [resolved]);
      expect(model.pendingRowIndexes()).toEqual([]);
      expect(model.keyboardRequestKey()).toBeNull();
      dispose();
    });
  });

  it("uses scoped request identities for keyboard targeting and restores the previous request", () => {
    createRoot((dispose) => {
      const model = createAgentPaneModel({} as ClientSession);
      const first = { ...request("approval", "same-item"), requestId: "provider-first" };
      const second = { ...first, threadId: "another-thread", requestId: "provider-second" };
      model.replace([first, second]);
      expect(model.keyboardRequestKey()).toBe(paneItemIdentity(second));
      expect(model.keyboardApprovalId()).toBe("provider-second");
      model.replace([first, second, { ...second, type: "approval-resolved", status: "denied" }]);
      expect(model.keyboardRequestKey()).toBe(paneItemIdentity(first));
      expect(model.keyboardApprovalId()).toBe("provider-first");
      expect(model.entries.at(-1)?.id).toBe(paneItemIdentity(first));
      dispose();
    });
  });

  it("does not treat an older request moved past a new prompt as the new turn's output", () => {
    createRoot((dispose) => {
      const model = createAgentPaneModel({} as ClientSession);
      const updates: AgentPaneUpdate[] = [
        request("input", "old-input"),
        { type: "user-message", providerId: "acp", turnId: "next-turn", text: "Next prompt" },
      ];
      model.replace(updates);
      expect(model.agentTurnStartId()).toBeNull();
      expect(model.agentTurnStartIndex()).toBeNull();
      const answer = { ...output("next-answer"), turnId: "next-turn" };
      model.replace([...updates, answer]);
      expect(model.agentTurnStartId()).toBe(paneItemIdentity(answer));
      expect(model.agentTurnStartIndex()).toBe(1);
      dispose();
    });
  });

  it("updates activity through its displayed path after moving a request", () => {
    createRoot((dispose) => {
      const model = createAgentPaneModel({} as ClientSession);
      const input = request("input", "input-1");
      const command: AgentPaneUpdate = {
        type: "item-started",
        providerId: "acp",
        threadId: "thread",
        turnId: "turn",
        itemId: "command",
        itemType: "commandExecution",
        summary: "git status",
        status: "inProgress",
      };
      const updates = [input, command];
      model.replace(updates);
      expect(model.entries[0]?.kind).toBe("activity");
      model.setActivityExpanded(model.entries[0]!.id, true);
      const completed = { ...command, type: "item-completed", status: "completed", text: "clean" };
      model.publish([input, completed], [completed]);
      expect(model.entries[0]?.details[0]?.detailText).toBe("clean");
      expect(model.entries[1]?.id).toBe(paneItemIdentity(input));
      dispose();
    });
  });
});

function request(kind: "approval" | "authentication" | "input", id: string): AgentPaneUpdate {
  return {
    type: `${kind}-requested`,
    providerId: "acp",
    threadId: "thread",
    turnId: "turn",
    itemId: id,
    requestId: id,
    status: "pending",
  };
}

function output(id: string): AgentPaneUpdate {
  return {
    type: "item-completed",
    providerId: "acp",
    threadId: "thread",
    turnId: "turn",
    itemId: id,
    itemType: "agentMessage",
    text: id,
    status: "completed",
  };
}
