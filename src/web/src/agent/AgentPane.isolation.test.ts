import { createMemo, createRoot, createSignal } from "solid-js";
import { describe, expect, it } from "vitest";
import type { AgentPaneUpdate } from "../bridge";

// The regression this guards: a message to a NON-focused slot must not recompute the focused pane's transcript.
// (Entry-level reconcile identity is a Solid browser-build behavior, so it is proven in the e2e/browser leg,
// not here — the node test env resolves solid-js to its server build with no store proxies.)
describe("focused-pane message isolation", () => {
  it("does not propagate to consumers when a background slot changes", () => {
    createRoot((dispose) => {
      const empty: AgentPaneUpdate[] = [];
      const focused: AgentPaneUpdate[] = [
        { type: "user-message", providerId: "codex", turnId: "t1", itemId: "u1", text: "q1" },
      ];
      const [records, setRecords] = createSignal<Record<string, AgentPaneUpdate[]>>({ focused });
      const focusedSlot = "focused";
      // Mirrors App.tsx's focusedAgentMessages memo: default (===) equality over the whole-record signal.
      const focusedMessages = createMemo<AgentPaneUpdate[]>(() => records()[focusedSlot] ?? empty);
      // A consumer standing in for the pane's transcript derivation — it must NOT re-run for a background change.
      let consumerRuns = 0;
      const consumer = createMemo(() => {
        consumerRuns += 1;
        return focusedMessages().length;
      });
      const first = focusedMessages();
      expect(consumer()).toBe(1);
      expect(consumerRuns).toBe(1);

      // A background slot ingests — the whole record changes reference, the focused array does not.
      setRecords((prev) => ({
        ...prev,
        background: [
          { type: "user-message", providerId: "codex", turnId: "t2", itemId: "u2", text: "bg" },
        ],
      }));

      expect(focusedMessages()).toBe(first);
      expect(consumer()).toBe(1);
      // The === equality on focusedMessages gated propagation: the transcript stand-in never recomputed.
      expect(consumerRuns).toBe(1);
      dispose();
    });
  });
});
