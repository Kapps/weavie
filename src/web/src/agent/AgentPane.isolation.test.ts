import { createRoot } from "solid-js";
import { describe, expect, it } from "vitest";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { createAgentPaneModel } from "./AgentPaneModel";

describe("agent pane model isolation", () => {
  it("projects updates into an unrendered session model", () => {
    createRoot((dispose) => {
      const model = createAgentPaneModel({} as ClientSession);
      const first = message("first", "First answer");
      model.publish([first]);

      expect(model.messages()).toEqual([first]);
      expect(model.entries.map((entry) => entry.text)).toEqual(["First answer"]);
      expect(model.revision()).toBe(1);
      expect(model.sectionLabels().get(model.entries[0]!.id)).toBe("Results");
      expect(model.turnActive()).toBe(false);

      dispose();
    });
  });

  it("keeps another session's projected state unchanged", () => {
    createRoot((dispose) => {
      const foreground = createAgentPaneModel({} as ClientSession);
      const background = createAgentPaneModel({} as ClientSession);
      foreground.publish([message("first", "First answer")]);
      const foregroundEntry = foreground.entries[0];
      const foregroundRevision = foreground.revision();

      const second = message("second", "Second answer");
      background.publish([second]);

      expect(background.entries.map((entry) => entry.text)).toEqual(["Second answer"]);
      expect(foreground.revision()).toBe(foregroundRevision);
      expect(foreground.entries[0]).toBe(foregroundEntry);
      dispose();
    });
  });
});

function message(itemId: string, text: string): AgentPaneUpdate {
  return {
    type: "item-completed",
    providerId: "codex",
    itemId,
    itemType: "agentMessage",
    status: "completed",
    text,
  };
}
