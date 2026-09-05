import { createRoot } from "solid-js";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { createAgentPaneModel } from "./AgentPaneModel";

const transcriptCalls = vi.hoisted(() => vi.fn());

vi.mock("../bridge", () => ({ registerSessionFeature: () => () => {} }));

vi.mock("./AgentPaneMessages", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./AgentPaneMessages")>();
  return {
    ...actual,
    projectAgentTranscript: (updates: readonly AgentPaneUpdate[]) => {
      transcriptCalls(updates.length);
      return actual.projectAgentTranscript(updates);
    },
  };
});

describe("agent pane model isolation", () => {
  beforeEach(() => transcriptCalls.mockClear());

  it("projects a background transcript before the session is selected", () => {
    createRoot((dispose) => {
      const model = createAgentPaneModel({} as ClientSession);
      const first = message("first", "First answer");
      model.replace([first]);

      expect(model.entries.map((entry) => entry.text)).toEqual(["First answer"]);

      const second = message("second", "Second answer");
      model.replace([first, second]);
      expect(model.entries.map((entry) => entry.text)).toEqual(["First answer", "Second answer"]);
      dispose();
    });
  });

  it("keeps another session's projected state unchanged", () => {
    createRoot((dispose) => {
      const foreground = createAgentPaneModel({} as ClientSession);
      const background = createAgentPaneModel({} as ClientSession);
      foreground.replace([message("first", "First answer")]);
      const foregroundEntry = foreground.entries[0];
      const foregroundRevision = foreground.revision();

      const second = message("second", "Second answer");
      background.replace([second]);

      expect(background.entries.map((entry) => entry.text)).toEqual(["Second answer"]);
      expect(foreground.revision()).toBe(foregroundRevision);
      expect(foreground.entries[0]).toBe(foregroundEntry);
      dispose();
    });
  });

  it("updates an established activity from changed items without refolding history", () => {
    createRoot((dispose) => {
      const model = createAgentPaneModel({} as ClientSession);
      const updates: AgentPaneUpdate[] = [
        { type: "user-message", providerId: "acp", turnId: "turn-1", text: "Work" },
        { ...command("command-0"), type: "item-started", status: "inProgress" },
      ];
      model.replace(updates);
      transcriptCalls.mockClear();
      const completed = command("command-0");
      updates[1] = completed;
      model.publish([...updates], [completed]);

      for (let index = 1; index < 1_000; index += 1) {
        const next = command(`command-${index}`);
        updates.push(next);
        model.publish([...updates], [next]);
      }

      expect(transcriptCalls).not.toHaveBeenCalled();
      expect(model.entries[1]?.summary).toBe("ran 1000 commands");
      expect(model.entries[1]?.detailCount).toBe(1_000);
      expect(model.entries[1]?.details).toEqual([]);
      model.setActivityExpanded(model.entries[1]!.id, true);
      expect(model.entries[1]?.details).toHaveLength(1_000);
      model.setActivityExpanded(model.entries[1]!.id, false);
      expect(model.entries[1]?.details).toEqual([]);
      dispose();
    });
  });

  it("keeps the latest remaining running step after a newer one completes incrementally", () => {
    createRoot((dispose) => {
      const model = createAgentPaneModel({} as ClientSession);
      const first = { ...command("command-1"), type: "item-started", status: "inProgress" };
      const second = { ...command("command-2"), type: "item-started", status: "inProgress" };
      const updates: AgentPaneUpdate[] = [
        { type: "user-message", providerId: "acp", turnId: "turn-1", text: "Work" },
        first,
        second,
      ];
      model.replace(updates);
      expect(model.entries[1]?.summary).toBe("running command: command-2");

      const completed = command("command-2");
      updates[2] = completed;
      model.publish([...updates], [completed]);

      expect(model.entries[1]?.summary).toBe("running command: command-1");
      dispose();
    });
  });

  it("updates streaming command output without refolding history", () => {
    createRoot((dispose) => {
      const model = createAgentPaneModel({} as ClientSession);
      const updates: AgentPaneUpdate[] = [
        { type: "user-message", providerId: "acp", turnId: "turn-1", text: "Work" },
        { ...command("command-1"), type: "item-started", status: "inProgress" },
      ];
      model.replace(updates);
      transcriptCalls.mockClear();

      for (let index = 1; index < 1_000; index += 1) {
        const delta: AgentPaneUpdate = {
          ...command("command-1"),
          type: "command-output-delta",
          status: "inProgress",
          text: "x".repeat(index),
        };
        updates[1] = delta;
        model.publish([...updates], [delta]);
      }

      expect(transcriptCalls).not.toHaveBeenCalled();
      expect(model.entries[1]?.details).toEqual([]);
      model.setActivityExpanded(model.entries[1]!.id, true);
      expect(model.entries[1]?.details[0]).toMatchObject({
        detailText: "x".repeat(999),
        status: "running",
      });
      const finalDelta: AgentPaneUpdate = {
        ...command("command-1"),
        type: "command-output-delta",
        status: "inProgress",
        text: "x".repeat(1_000),
      };
      updates[1] = finalDelta;
      model.publish([...updates], [finalDelta]);
      expect(model.entries[1]?.details[0]).toMatchObject({
        detailText: "x".repeat(1_000),
        status: "running",
      });
      dispose();
    });
  });

  it("promotes a completed plan out of an established activity", () => {
    createRoot((dispose) => {
      const model = createAgentPaneModel({} as ClientSession);
      const updates: AgentPaneUpdate[] = [
        { type: "user-message", providerId: "acp", turnId: "turn-1", text: "Plan" },
        command("command-1"),
      ];
      model.replace(updates);
      const draft = plan("plan-delta", "# Draft plan");
      updates.push(draft);
      model.publish([...updates], [draft]);
      transcriptCalls.mockClear();

      for (let index = 1; index <= 100; index += 1) {
        const delta = plan("plan-delta", `# Draft plan ${index}`);
        updates[2] = delta;
        model.publish([...updates], [delta]);
      }
      expect(transcriptCalls).not.toHaveBeenCalled();

      const completed = plan("item-completed", "# Final plan");
      updates[2] = completed;
      model.publish([...updates], [completed]);

      expect(transcriptCalls).toHaveBeenCalledTimes(1);
      expect(model.entries.find((entry) => entry.kind === "plan")).toMatchObject({
        label: "Plan",
        summary: "Ready to review in the editor",
      });
      dispose();
    });
  });
});

function message(itemId: string, text: string): AgentPaneUpdate {
  return {
    type: "item-completed",
    providerId: "acp",
    itemId,
    itemType: "agentMessage",
    status: "completed",
    text,
  };
}

function command(itemId: string): AgentPaneUpdate {
  return {
    type: "item-completed",
    providerId: "acp",
    turnId: "turn-1",
    itemId,
    itemType: "commandExecution",
    status: "completed",
    summary: itemId,
  };
}

function plan(type: "item-completed" | "plan-delta", text: string): AgentPaneUpdate {
  return {
    type,
    providerId: "acp",
    threadId: "thread-1",
    turnId: "turn-1",
    itemId: "plan-1",
    itemType: "plan",
    status: type === "item-completed" ? "completed" : "inProgress",
    text,
  };
}
