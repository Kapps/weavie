import { describe, expect, it } from "vitest";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { assistantSectionLabel } from "./AgentTranscript";

function message(id: string, tone: "assistant" | "user"): AgentTranscriptEntry {
  return {
    actionMessage: null,
    details: [],
    id,
    kind: "message",
    label: tone === "assistant" ? "Codex" : "You",
    status: null,
    summary: null,
    text: id,
    tone,
  };
}

describe("assistantSectionLabel", () => {
  it("labels the latest assistant message as updates while the turn is active", () => {
    const entries = [message("prompt", "user"), message("progress", "assistant")];

    expect(assistantSectionLabel(entries, 1, true)).toBe("Updates");
  });

  it("labels the latest assistant message as results once the turn completes", () => {
    const entries = [message("prompt", "user"), message("done", "assistant")];

    expect(assistantSectionLabel(entries, 1, false)).toBe("Results");
  });

  it("keeps a prior turn's final assistant message labeled as results", () => {
    const entries = [
      message("first prompt", "user"),
      message("first result", "assistant"),
      message("second prompt", "user"),
      message("progress", "assistant"),
    ];

    expect(assistantSectionLabel(entries, 1, true)).toBe("Results");
    expect(assistantSectionLabel(entries, 3, true)).toBe("Updates");
  });
});
