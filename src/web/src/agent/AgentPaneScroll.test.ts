import { Scrollable } from "@codingame/monaco-vscode-api/vscode/vs/base/common/scrollable";
import { createRoot } from "solid-js";
import { expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";
import { createAgentPaneScroll } from "./AgentPaneScroll";
import type { TranscriptViewport } from "./AgentTranscriptViewport";

vi.mock("../commands/registry", () => ({ registerCommand: () => () => {} }));

it("resumes following on downward input at a boundary reached through geometry correction", () => {
  const model = new Scrollable({
    forceIntegerValues: false,
    smoothScrollDuration: 125,
    scheduleAtNextAnimationFrame: () => {
      throw new Error("Immediate input must not schedule an animation");
    },
  });
  model.setScrollDimensions(
    { width: 400, scrollWidth: 400, height: 400, scrollHeight: 2000 },
    false,
  );
  model.setScrollPositionNow({ scrollTop: 500 });
  const viewport: TranscriptViewport = {
    height: () => model.getScrollDimensions().height,
    contentHeight: () => model.getScrollDimensions().scrollHeight,
    offset: () => model.getCurrentScrollPosition().scrollTop,
    itemTop: () => 0,
    jumpTo: (offset) => model.setScrollPositionNow({ scrollTop: offset }),
    snapshot: () => new Map(),
    readingPosition: () => null,
  };
  const { controller, dispose } = createRoot((dispose) => ({
    controller: createAgentPaneScroll(
      {} as ClientSession,
      () => viewport,
      () => 20,
      () => null,
      () => false,
      false,
    ),
    dispose,
  }));
  const events = model.onScroll((event) => {
    controller.onWillScroll(event);
    controller.onDidScroll(event);
  });
  try {
    model.setScrollGeometry({ scrollHeight: 780 }, { scrollLeft: 0, scrollTop: 0 });
    expect(viewport.offset()).toBe(380);
    expect(controller.followingLatest()).toBe(false);
    model.setScrollPositionFromInput({ scrollTop: viewport.offset() + 120 }, false);
    expect(viewport.offset()).toBe(380);
    expect(controller.followingLatest()).toBe(true);
  } finally {
    events.dispose();
    model.dispose();
    dispose();
  }
});
