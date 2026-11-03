import { createRoot } from "solid-js";
import { describe, expect, it } from "vitest";
import { createAgentPaneLayout } from "./AgentPaneLayout";
import type { AgentTranscriptEntry, AgentTranscriptSnapshot } from "./AgentPaneTranscriptTypes";
import { emptyTranscript } from "./AgentTranscriptSnapshot";

describe("agent pane layout", () => {
  it("measures from the bottom: virtual index 0 is the newest entry", () => {
    createRoot((dispose) => {
      const layout = createAgentPaneLayout(
        () => transcript(["a", "b", "c", "d", "e", "f"], [5]),
        () => {
          throw new Error("No transcript change reaches the virtualizer here.");
        },
      );
      expect(layout.count()).toBe(6);
      expect([0, 5].map(layout.flip)).toEqual([5, 0]);
      expect(layout.entryAt(0)?.id).toBe("f");
      expect(layout.entryAt(5)?.id).toBe("a");
      expect(layout.entryAt(6)).toBeUndefined();
      expect(layout.itemKey()(0)).toBe("f");
      expect(layout.entryForKey("b").id).toBe("b");
      expect(layout.virtualIndexOf("b")).toBe(4);
      expect(layout.virtualIndexOf("missing")).toBeUndefined();

      const range = { startIndex: 2, endIndex: 3, overscan: 0, count: 6 };
      expect(layout.rangeExtractor()(range)).toEqual([0, 2, 3]);
      dispose();
    });
  });
});

function transcript(ids: string[], pendingRowIndexes: number[]): AgentTranscriptSnapshot {
  return {
    ...emptyTranscript,
    entries: ids.map((id) => ({ id }) as AgentTranscriptEntry),
    pendingRowIndexes,
  };
}
