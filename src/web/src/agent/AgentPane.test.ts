import { describe, expect, it } from "vitest";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { computeSectionLabels, latestAgentTurnStartId } from "./AgentTranscriptLabels";

function message(id: string, tone: "assistant" | "user"): AgentTranscriptEntry {
  return {
    actionMessage: null,
    detailCount: 0,
    details: [],
    id,
    kind: "message",
    label: tone === "assistant" ? "ACP" : "You",
    status: null,
    streaming: false,
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

  it("treats a completed plan as the turn result", () => {
    const entries: AgentTranscriptEntry[] = [
      message("prompt", "user"),
      {
        actionMessage: null,
        detailCount: 0,
        details: [],
        id: "plan",
        kind: "plan",
        label: "Plan",
        status: null,
        streaming: false,
        summary: "Ready to review in the editor",
        text: null,
        tone: "assistant",
      },
    ];

    expect(computeSectionLabels(entries, false).get("plan")).toBe("Results");
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

describe("latestAgentTurnStartId", () => {
  it("chooses the first agent output after the latest turn boundary", () => {
    const firstPrompt = { ...message("first prompt", "user"), turnStart: true as const };
    const secondPrompt = { ...message("second prompt", "user"), turnStart: true as const };
    const entries = [
      firstPrompt,
      message("first result", "assistant"),
      secondPrompt,
      message("progress", "assistant"),
      message("final result", "assistant"),
    ];

    expect(latestAgentTurnStartId(entries)).toBe("progress");
  });

  it("returns null rather than reusing an earlier turn when the latest has no response", () => {
    const entries = [
      { ...message("first prompt", "user"), turnStart: true as const },
      message("first result", "assistant"),
      { ...message("second prompt", "user"), turnStart: true as const },
      message("steer", "user"),
    ];

    expect(latestAgentTurnStartId(entries)).toBeNull();
  });

  it("anchors live output while lifecycle state keeps navigation unavailable", () => {
    const entries = [
      { ...message("prompt", "user"), turnStart: true as const },
      message("earlier update", "assistant"),
      { ...message("partial response", "assistant"), streaming: true },
    ];

    expect(latestAgentTurnStartId(entries)).toBe("earlier update");
  });
});
