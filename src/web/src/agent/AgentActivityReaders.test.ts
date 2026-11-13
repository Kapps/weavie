import { createRoot } from "solid-js";
import { expect, it } from "vitest";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { createTestAgentPaneModel } from "./AgentPaneTestModel";
import { holdTranscript } from "./AgentTranscriptSnapshot";

it("keeps subagent activity available until both pane and tab readers close", () =>
  createRoot((dispose) => {
    const model = createTestAgentPaneModel({} as ClientSession);
    const tool: AgentPaneUpdate = {
      providerId: "acp",
      conversationId: "reviewer",
      threadId: "reviewer",
      turnId: "review",
      type: "item-completed",
      itemType: "tool",
      itemId: "read",
      status: "completed",
      text: "original output",
    };
    const rows: AgentPaneUpdate[] = [
      {
        providerId: "acp",
        conversationId: "reviewer",
        type: "subagent-started",
        summary: "Reviewer",
      },
      tool,
    ];
    model.replace(rows);
    const activity = () =>
      model.conversation("reviewer")!.asideEntries!.find((entry) => entry.kind === "activity")!;
    expect(activity().activityDetails!.snapshot()).toEqual([]);
    const source = activity().activityDetails!;
    const transcript = model.conversation("reviewer")!.conversation!.transcript();
    const tab = source.retain();
    expect(model.conversation("reviewer")!.conversation!.transcript()).toBe(transcript);
    expect(source.snapshot()[0]?.detailText).toBe("original output");
    const pane = source.retain();
    tab.release();
    expect(source.snapshot()[0]?.detailText).toBe("original output");
    const held = holdTranscript(model.transcript());
    const heldActivity = held.entries[0]!.asideEntries!.find((entry) => entry.kind === "activity")!;
    expect(heldActivity.activityDetails).toBeUndefined();

    rows[1] = { ...tool, text: "updated output" };
    model.publish([...rows], [rows[1]]);
    expect(source.snapshot()[0]?.detailText).toBe("updated output");
    expect(heldActivity.details[0]?.detailText).toBe("original output");
    model.replace([...rows]);
    expect(source.snapshot()[0]?.detailText).toBe("updated output");

    const beforeRelease = model.conversation("reviewer")!.conversation!.transcript();
    pane.release();
    expect(model.conversation("reviewer")!.conversation!.transcript()).toBe(beforeRelease);
    expect(activity().activityDetails!.snapshot()).toEqual([]);
    const reopened = source.retain();
    expect(source.snapshot()[0]?.detailText).toBe("updated output");
    model.reset();
    reopened.release();
    expect(model.conversation("reviewer")).toBeUndefined();
    const retired = source.retain();
    expect(retired.steps()[0]?.detailText).toBe("updated output");
    retired.release();
    dispose();
  }));
