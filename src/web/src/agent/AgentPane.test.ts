import { describe, expect, it } from "vitest";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { computeSectionLabels } from "./AgentTranscriptLabels";

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

describe("computeSectionLabels", () => {
  it("labels the latest assistant message as updates while the turn is active", () => {
    const entries = [message("prompt", "user"), message("progress", "assistant")];

    expect(computeSectionLabels(entries, true).get("progress")).toBe("Updates");
  });

  it("labels the latest assistant message as results once the turn completes", () => {
    const entries = [message("prompt", "user"), message("done", "assistant")];

    expect(computeSectionLabels(entries, false).get("done")).toBe("Results");
  });

  it("keeps a prior turn's final assistant message labeled as results", () => {
    const entries = [
      message("first prompt", "user"),
      message("first result", "assistant"),
      message("second prompt", "user"),
      message("progress", "assistant"),
    ];

    const labels = computeSectionLabels(entries, true);
    expect(labels.get("first result")).toBe("Results");
    expect(labels.get("progress")).toBe("Updates");
  });

  it("omits earlier assistant messages and non-assistant entries from the map", () => {
    const entries = [
      message("prompt", "user"),
      message("earlier", "assistant"),
      message("latest", "assistant"),
    ];

    const labels = computeSectionLabels(entries, true);
    expect(labels.has("earlier")).toBe(false);
    expect(labels.has("prompt")).toBe(false);
    expect(labels.get("latest")).toBe("Updates");
    expect(labels.size).toBe(1);
  });
});
