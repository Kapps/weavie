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
    scheduled[0]?.();

    expect(messages).toEqual([update("item-completed", "final")]);
  });

  it("keeps equal turn and item ids distinct across threads", () => {
    const scheduled: Array<() => void> = [];
    const accumulator = new AgentPaneAccumulator((callback) => scheduled.push(callback));
    let messages: AgentPaneUpdate[] = [];
    const publish = (value: AgentPaneUpdate[]): void => {
      messages = value;
    };
    accumulator.ingest(
      "slot-1",
      { ...update("agent-message-delta", "alpha"), threadId: "thread-a" },
      publish,
    );
    accumulator.ingest(
      "slot-1",
      { ...update("agent-message-delta", "beta"), threadId: "thread-b" },
      publish,
    );
    for (const flush of scheduled) flush();

    expect(messages.map((message) => [message.threadId, message.text])).toEqual([
      ["thread-a", "alpha"],
      ["thread-b", "beta"],
    ]);
  });

  it("does not alias missing fields or delimiter-bearing opaque ids", () => {
    const scheduled: Array<() => void> = [];
    const accumulator = new AgentPaneAccumulator((callback) => scheduled.push(callback));
    let messages: AgentPaneUpdate[] = [];
    const publish = (value: AgentPaneUpdate[]): void => {
      messages = value;
    };
    const collisions: AgentPaneUpdate[] = [
      { ...update("agent-message-delta", "missing-thread"), threadId: null, turnId: "session" },
      { ...update("agent-message-delta", "missing-turn"), threadId: "thread", turnId: null },
      { ...update("agent-message-delta", "thread-delimiter"), threadId: "a:b", turnId: "c" },
      { ...update("agent-message-delta", "turn-delimiter"), threadId: "a", turnId: "b:c" },
    ];
    for (const collision of collisions) accumulator.ingest("slot-1", collision, publish);
    for (const flush of scheduled) flush();

    expect(messages.map((message) => message.text)).toEqual(
      collisions.map((message) => message.text),
    );
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
