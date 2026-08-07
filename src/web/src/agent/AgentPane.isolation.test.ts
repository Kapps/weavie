import { createRoot } from "solid-js";
import { describe, expect, it } from "vitest";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { createAgentPaneModel } from "./AgentPaneModel";

describe("agent pane model isolation", () => {
  it("defers background transcript projection until the session is attached", () => {
    createRoot((dispose) => {
      const model = createAgentPaneModel({} as ClientSession);
      const first = message("first", "First answer");
      model.publish([first]);

      expect(model.messages()).toEqual([first]);
      expect(model.entries).toHaveLength(0);

      const detach = model.attach();
      expect(model.entries.map((entry) => entry.text)).toEqual(["First answer"]);
      detach();

      const second = message("second", "Second answer");
      model.publish([first, second]);
      expect(model.messages()).toEqual([first, second]);
      expect(model.entries.map((entry) => entry.text)).toEqual(["First answer"]);

      const detachAgain = model.attach();
      expect(model.entries.map((entry) => entry.text)).toEqual(["First answer", "Second answer"]);
      detachAgain();
      dispose();
    });
  });

  it("keeps another session's projected state unchanged while unattached", () => {
    createRoot((dispose) => {
      const foreground = createAgentPaneModel({} as ClientSession);
      const background = createAgentPaneModel({} as ClientSession);
      const detach = foreground.attach();
      foreground.publish([message("first", "First answer")]);
      const foregroundEntry = foreground.entries[0];
      const foregroundRevision = foreground.revision();

      const second = message("second", "Second answer");
      background.publish([second]);

      expect(background.entries).toHaveLength(0);
      expect(foreground.revision()).toBe(foregroundRevision);
      expect(foreground.entries[0]).toBe(foregroundEntry);
      detach();
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
