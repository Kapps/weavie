import { describe, expect, it } from "vitest";
import type { AgentPaneUpdate } from "../bridge";
import { AgentPaneAccumulator } from "./AgentPaneAccumulator";

describe("AgentPaneAccumulator", () => {
  it("buffers chunks and publishes one cumulative item per render cadence", () => {
    const scheduled: Array<() => void> = [];
    const accumulator = new AgentPaneAccumulator((callback) => scheduled.push(callback));
    let messages: AgentPaneUpdate[] = [];
    for (let index = 0; index < 1_000; index += 1) {
      accumulator.ingest("slot-1", update("agent-message-delta", "x"), (value) => {
        messages = value;
      });
    }

    expect(scheduled).toHaveLength(1);
    scheduled[0]?.();
    expect(messages).toHaveLength(1);
    expect(messages[0]?.text).toHaveLength(1_000);
  });

  it("replaces buffered state with completion without publishing a stale frame", () => {
    const scheduled: Array<() => void> = [];
    const accumulator = new AgentPaneAccumulator((callback) => scheduled.push(callback));
    let messages: AgentPaneUpdate[] = [];
    const publish = (value: AgentPaneUpdate[]): void => {
      messages = value;
    };
    accumulator.ingest("slot-1", update("item-started", ""), publish);
    accumulator.ingest("slot-1", update("command-output-delta", "part"), publish);
    accumulator.ingest("slot-1", update("item-completed", "final"), publish);
    // One flush, not one publish per message — the completion supersedes the buffered delta in the snapshot.
    expect(scheduled).toHaveLength(1);
    scheduled[0]?.();

    expect(messages).toEqual([update("item-completed", "final")]);
  });

  it("coalesces a non-delta burst to a single publish per frame", () => {
    const scheduled: Array<() => void> = [];
    const accumulator = new AgentPaneAccumulator((callback) => scheduled.push(callback));
    let publishes = 0;
    let messages: AgentPaneUpdate[] = [];
    const publish = (value: AgentPaneUpdate[]): void => {
      publishes += 1;
      messages = value;
    };
    // Distinct items so nothing coalesces at the item level: without per-frame batching this would publish 500×.
    for (let index = 0; index < 500; index += 1) {
      accumulator.ingest(
        "slot-1",
        { ...update("item-started", ""), itemId: `item-${index}` },
        publish,
      );
    }

    expect(publishes).toBe(0);
    expect(scheduled).toHaveLength(1);
    scheduled[0]?.();
    expect(publishes).toBe(1);
    expect(messages).toHaveLength(500);
  });

  it("makes an approval request visible after the next flush", () => {
    const scheduled: Array<() => void> = [];
    const accumulator = new AgentPaneAccumulator((callback) => scheduled.push(callback));
    let messages: AgentPaneUpdate[] = [];
    accumulator.ingest("slot-1", update("approval-requested", ""), (value) => {
      messages = value;
    });

    expect(messages).toHaveLength(0);
    scheduled[0]?.();
    expect(messages).toEqual([update("approval-requested", "")]);
  });

  it("resets to empty and a flush queued before the reset does not republish", () => {
    const scheduled: Array<() => void> = [];
    const accumulator = new AgentPaneAccumulator((callback) => scheduled.push(callback));
    let messages: AgentPaneUpdate[] = [update("item-completed", "stale")];
    const publish = (value: AgentPaneUpdate[]): void => {
      messages = value;
    };
    accumulator.ingest("slot-1", update("item-started", ""), publish);
    accumulator.reset("slot-1", publish);
    expect(messages).toEqual([]);
    scheduled[0]?.();
    expect(messages).toEqual([]);
  });
});

function update(type: string, text: string): AgentPaneUpdate {
  return {
    type,
    providerId: "codex",
    turnId: "turn-1",
    itemId: "item-1",
    itemType: "commandExecution",
    text,
  };
}
