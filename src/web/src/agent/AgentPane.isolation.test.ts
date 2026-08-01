import { createMemo, createRoot, createSignal } from "solid-js";
import { describe, expect, it } from "vitest";
import type { AgentPaneUpdate, ClientSession } from "../bridge";

function owner(slot: string, incarnation: string): ClientSession {
  return {
    address: { slot, incarnation },
    connection: { id: "local" },
  } as ClientSession;
}

// The regression this guards: a message to a non-selected owner must not recompute the selected pane's transcript.
// (Entry-level reconcile identity is a Solid browser-build behavior, so it is proven in the e2e/browser leg,
// not here — the node test env resolves solid-js to its server build with no store proxies.)
describe("selected-pane message isolation", () => {
  it("isolates background owners and new incarnations of the same slot", () => {
    createRoot((dispose) => {
      const empty: AgentPaneUpdate[] = [];
      const selectedMessages: AgentPaneUpdate[] = [
        { type: "user-message", providerId: "codex", turnId: "t1", itemId: "u1", text: "q1" },
      ];
      const selected = owner("shared", "incarnation-1");
      const replacement = owner("shared", "incarnation-2");
      const background = owner("background", "incarnation-1");
      const [records, setRecords] = createSignal(
        new Map<ClientSession, AgentPaneUpdate[]>([[selected, selectedMessages]]),
      );
      const focusedMessages = createMemo<AgentPaneUpdate[]>(() => records().get(selected) ?? empty);
      // A consumer standing in for the pane's transcript derivation — it must NOT re-run for a background change.
      let consumerRuns = 0;
      const consumer = createMemo(() => {
        consumerRuns += 1;
        return focusedMessages().length;
      });
      const first = focusedMessages();
      expect(consumer()).toBe(1);
      expect(consumerRuns).toBe(1);

      setRecords((previous) =>
        new Map(previous).set(background, [
          {
            type: "user-message",
            providerId: "codex",
            turnId: "t2",
            itemId: "u2",
            text: "bg",
          },
        ]),
      );
      setRecords((previous) =>
        new Map(previous).set(replacement, [
          { type: "user-message", providerId: "codex", turnId: "t2", itemId: "u2", text: "bg" },
        ]),
      );

      expect(focusedMessages()).toBe(first);
      expect(records().get(replacement)).not.toBe(records().get(selected));
      expect(consumer()).toBe(1);
      expect(consumerRuns).toBe(1);
      dispose();
    });
  });
});
