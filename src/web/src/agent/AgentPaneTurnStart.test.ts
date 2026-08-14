import { describe, expect, it } from "vitest";
import type { AgentPaneUpdate } from "../bridge";
import { toAgentTranscript } from "./AgentPaneMessages";

describe("agent turn starts", () => {
  it("marks prompts and image-only turns, but not prompt attachments or steers", () => {
    const messages: AgentPaneUpdate[] = [
      input("user-message", null, "prompt", "Inspect this"),
      input("user-image", null, "prompt-image", "/tmp/prompt.png"),
      input("user-steer", "turn-1", "live-steer", "Keep going"),
      input("user-message", "turn-1", "restored-steer", "Keep going after restore"),
      input("user-image", "turn-1", "steer-image", "/tmp/steer.png"),
      {
        type: "item-completed",
        providerId: "acp",
        threadId: "thread-1",
        turnId: "turn-1",
        itemId: "answer",
        itemType: "agentMessage",
        text: "Done",
      },
      input("user-image", "turn-2", "image-only", "/tmp/only.png"),
    ];

    expect(
      toAgentTranscript(messages)
        .filter((entry) => entry.tone === "user")
        .map((entry) => [entry.label, entry.turnStart === true]),
    ).toEqual([
      ["You", true],
      ["Image", false],
      ["Steer", false],
      ["You", false],
      ["Image", false],
      ["Image", true],
    ]);
  });
});

function input(
  type: "user-image" | "user-message" | "user-steer",
  turnId: string | null,
  itemId: string,
  text: string,
): AgentPaneUpdate {
  return { type, providerId: "acp", threadId: "thread-1", turnId, itemId, text };
}
