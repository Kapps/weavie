import { describe, expect, it } from "vitest";
import type { AgentPaneUpdate } from "../bridge";
import { toAgentTranscript } from "./AgentPaneTestModel";
import { backgroundStatus } from "./background-format";

describe("background work", () => {
  it("states a running item's elapsed time and reported usage, and a finished item's final duration", () => {
    const usage = { totalTokens: 182_400, toolUses: 9, durationMs: 1 };
    expect(backgroundStatus("running", 0, null, 252_000, usage)).toBe(
      "running · 4m 12s · 9 tools · 182k tokens",
    );
    expect(backgroundStatus("completed", 0, 5_000, 999_000, null)).toBe("completed · 5s");
    expect(backgroundStatus("cancelled", 0, null, 999_000, null)).toBe("cancelled");
  });

  it("projects a workflow card that its completion replaces in place", () => {
    const started: AgentPaneUpdate = {
      providerId: "acp",
      threadId: "primary",
      turnId: "1",
      type: "item-started",
      itemId: "task:wf",
      itemType: "backgroundTask",
      summary: "code-review",
      status: "running",
    };
    const [running] = toAgentTranscript([started]);
    expect(running).toMatchObject({ kind: "workflow", summary: "code-review", status: "running" });
    const entries = toAgentTranscript([
      started,
      { ...started, type: "item-completed", status: "stopped" },
    ]);
    expect(entries).toHaveLength(1);
    expect(entries[0]).toMatchObject({ kind: "workflow", status: "stopped" });
  });
});
