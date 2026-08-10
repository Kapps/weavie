import { describe, expect, it } from "vitest";
import type { AgentPaneHistoryFragment, AgentPaneUpdate, AgentPaneWireUpdate } from "../bridge";
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

  it("coalesces a non-delta burst to a single publish per frame", () => {
    const scheduled: Array<() => void> = [];
    const accumulator = new AgentPaneAccumulator((callback) => scheduled.push(callback));
    let publishes = 0;
    let messages: AgentPaneUpdate[] = [];
    let changes: AgentPaneUpdate[] = [];
    const publish = (value: AgentPaneUpdate[], changed: AgentPaneUpdate[]): void => {
      publishes += 1;
      messages = value;
      changes = changed;
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
    expect(changes).toHaveLength(500);
  });

  it("does not build an incremental change list for an initial batch", () => {
    const scheduled: Array<() => void> = [];
    const accumulator = new AgentPaneAccumulator((callback) => scheduled.push(callback));
    let messages: AgentPaneUpdate[] = [];
    let changes: AgentPaneUpdate[] = [];
    accumulator.ingestBatch(
      "slot-1",
      Array.from({ length: 500 }, (_, index) => ({
        ...update("item-completed", ""),
        itemId: `item-${index}`,
      })),
      (value, changed) => {
        messages = value;
        changes = changed;
      },
    );

    expect(scheduled).toHaveLength(1);
    scheduled[0]?.();
    expect(messages).toHaveLength(500);
    expect(changes).toEqual([]);
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

  it("merges older history pages behind live updates without duplicating ordinals", () => {
    const scheduled: Array<() => void> = [];
    const accumulator = new AgentPaneAccumulator((callback) => scheduled.push(callback));
    let messages: AgentPaneUpdate[] = [];
    const publish = (value: AgentPaneUpdate[]): void => {
      messages = value;
    };
    accumulator.ingest("slot-1", wireUpdate(2, 4, 4, "live"), publish);
    scheduled[0]?.();

    accumulator.mergeHistory(
      "slot-1",
      2,
      history(wireUpdate(2, 2, 2, "second"), wireUpdate(2, 3, 3, "third")),
      false,
      publish,
    );
    accumulator.mergeHistory("slot-1", 2, history(wireUpdate(2, 1, 1, "first")), true, publish);

    expect(messages.map((message) => message.text)).toEqual(["first", "second", "third", "live"]);
  });

  it("publishes only the newest and completed snapshots across many history pages", () => {
    const accumulator = new AgentPaneAccumulator((callback) => callback());
    const snapshots: AgentPaneUpdate[][] = [];
    const publish = (value: AgentPaneUpdate[]): void => {
      snapshots.push(value);
    };

    for (let ordinal = 100; ordinal > 0; ordinal -= 1) {
      accumulator.mergeHistory(
        "slot-1",
        1,
        history(wireUpdate(1, ordinal, ordinal, `message-${ordinal}`)),
        ordinal === 1,
        publish,
      );
    }

    expect(snapshots).toHaveLength(2);
    expect(snapshots[0]?.map((message) => message.text)).toEqual(["message-100"]);
    expect(snapshots[1]).toHaveLength(100);
    expect(snapshots[1]?.[0]?.text).toBe("message-1");
    expect(snapshots[1]?.[99]?.text).toBe("message-100");
  });

  it("does not republish an unchanged completed history baseline", () => {
    const accumulator = new AgentPaneAccumulator((callback) => callback());
    const snapshots: AgentPaneUpdate[][] = [];
    const publish = (value: AgentPaneUpdate[]): void => {
      snapshots.push(value);
    };

    accumulator.mergeHistory("slot-1", 1, history(wireUpdate(1, 1, 1, "first")), true, publish);
    accumulator.mergeHistory("slot-1", 1, [], true, publish);

    expect(snapshots).toHaveLength(1);
    expect(snapshots[0]?.map((message) => message.text)).toEqual(["first"]);
  });

  it("ignores a stale history page after a new transcript generation arrives", () => {
    const accumulator = new AgentPaneAccumulator((callback) => callback());
    let messages: AgentPaneUpdate[] = [];
    const publish = (value: AgentPaneUpdate[]): void => {
      messages = value;
    };

    accumulator.ingest("slot-1", wireUpdate(3, 1, 1, "replacement"), publish);
    accumulator.mergeHistory("slot-1", 2, history(wireUpdate(2, 1, 1, "stale")), true, publish);

    expect(messages.map((message) => message.text)).toEqual(["replacement"]);
  });

  it("keeps the cumulative history revision over its buffered live delta", () => {
    const scheduled: Array<() => void> = [];
    const accumulator = new AgentPaneAccumulator((callback) => scheduled.push(callback));
    let messages: AgentPaneUpdate[] = [];
    const publish = (value: AgentPaneUpdate[]): void => {
      messages = value;
    };

    accumulator.ingest("slot-1", wireDelta(1, 1, 1, "a"), publish);
    scheduled.shift()?.();
    accumulator.ingest("slot-1", wireDelta(1, 1, 2, "b"), publish);
    accumulator.mergeHistory("slot-1", 1, history(wireDelta(1, 1, 2, "ab")), true, publish);

    expect(messages.map((message) => message.text)).toEqual(["ab"]);
  });

  it("prepends cumulative history to a newer live delta that arrived first", () => {
    const accumulator = new AgentPaneAccumulator((callback) => callback());
    let messages: AgentPaneUpdate[] = [];
    const publish = (value: AgentPaneUpdate[]): void => {
      messages = value;
    };

    accumulator.ingest("slot-1", wireDelta(1, 1, 3, "c"), publish);
    accumulator.mergeHistory("slot-1", 1, history(wireDelta(1, 1, 2, "ab")), true, publish);
    accumulator.ingest("slot-1", wireDelta(1, 1, 4, "d"), publish);
    accumulator.mergeHistory("slot-1", 1, history(wireDelta(1, 1, 2, "ab")), true, publish);

    expect(messages.map((message) => message.text)).toEqual(["abcd"]);
  });

  it("preserves a newer live delta while its older cumulative history is fragmented", () => {
    const accumulator = new AgentPaneAccumulator((callback) => callback());
    let messages: AgentPaneUpdate[] = [];
    const publish = (value: AgentPaneUpdate[]): void => {
      messages = value;
    };

    accumulator.ingest("slot-1", wireDelta(1, 1, 3, "c"), publish);
    const [prefix, suffix] = splitHistory(wireDelta(1, 1, 2, "ab"));
    accumulator.mergeHistory("slot-1", 1, [suffix], false, publish);
    accumulator.mergeHistory("slot-1", 1, [prefix], true, publish);

    expect(messages.map((message) => message.text)).toEqual(["abc"]);
  });

  it("rejects a delayed live delta at the history revision", () => {
    const accumulator = new AgentPaneAccumulator((callback) => callback());
    let messages: AgentPaneUpdate[] = [];
    const publish = (value: AgentPaneUpdate[]): void => {
      messages = value;
    };

    accumulator.mergeHistory("slot-1", 1, history(wireDelta(1, 1, 2, "ab")), true, publish);
    accumulator.ingest("slot-1", wireDelta(1, 1, 2, "b"), publish);

    expect(messages.map((message) => message.text)).toEqual(["ab"]);
  });

  it("appends a newer live delta after cumulative history", () => {
    const accumulator = new AgentPaneAccumulator((callback) => callback());
    let messages: AgentPaneUpdate[] = [];
    const publish = (value: AgentPaneUpdate[]): void => {
      messages = value;
    };

    accumulator.mergeHistory("slot-1", 1, history(wireDelta(1, 1, 2, "ab")), true, publish);
    accumulator.ingest("slot-1", wireDelta(1, 1, 3, "c"), publish);

    expect(messages.map((message) => message.text)).toEqual(["abc"]);
  });

  it("publishes a fragmented history record only after every text range arrives", () => {
    const accumulator = new AgentPaneAccumulator((callback) => callback());
    let messages: AgentPaneUpdate[] = [];
    const publish = (value: AgentPaneUpdate[]): void => {
      messages = value;
    };

    const [prefix, suffix] = splitHistory(wireUpdate(1, 1, 1, "abcd"));
    accumulator.mergeHistory("slot-1", 1, [suffix], false, publish);
    expect(messages).toEqual([]);
    accumulator.mergeHistory("slot-1", 1, [prefix], true, publish);

    expect(messages.map((message) => message.text)).toEqual(["abcd"]);
  });

  it("discards incomplete fragments when a newer record revision completes", () => {
    const accumulator = new AgentPaneAccumulator((callback) => callback());
    let messages: AgentPaneUpdate[] = [];
    const publish = (value: AgentPaneUpdate[]): void => {
      messages = value;
    };

    const [, oldSuffix] = splitHistory(wireUpdate(1, 1, 1, "abcd"));
    accumulator.mergeHistory("slot-1", 1, [oldSuffix], false, publish);
    const [prefix, suffix] = splitHistory(wireUpdate(1, 1, 2, "abcde"));
    accumulator.mergeHistory("slot-1", 1, [suffix], false, publish);
    accumulator.mergeHistory("slot-1", 1, [prefix], true, publish);

    expect(messages.map((message) => message.text)).toEqual(["abcde"]);
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

function wireUpdate(
  generation: number,
  ordinal: number,
  revision: number,
  text: string,
): AgentPaneWireUpdate {
  return {
    ...update("item-completed", text),
    generation,
    ordinal,
    revision,
    textOffset: 0,
    textLength: text.length,
    itemId: `item-${ordinal}`,
  };
}

function wireDelta(
  generation: number,
  ordinal: number,
  revision: number,
  text: string,
): AgentPaneWireUpdate {
  return {
    ...wireUpdate(generation, ordinal, revision, text),
    type: "agent-message-delta",
    itemId: "item-1",
  };
}

function history(...messages: AgentPaneWireUpdate[]): AgentPaneHistoryFragment[] {
  return messages.map((message) => fragment(message, JSON.stringify(message), 0));
}

function splitHistory(
  message: AgentPaneWireUpdate,
): [AgentPaneHistoryFragment, AgentPaneHistoryFragment] {
  const json = JSON.stringify(message);
  const offset = Math.floor(json.length / 2);
  return [
    fragment(message, json.slice(0, offset), 0),
    fragment(message, json.slice(offset), offset),
  ];
}

function fragment(
  message: AgentPaneWireUpdate,
  json: string,
  jsonOffset: number,
): AgentPaneHistoryFragment {
  return {
    generation: message.generation,
    ordinal: message.ordinal,
    revision: message.revision,
    jsonOffset,
    jsonLength: JSON.stringify(message).length,
    json,
  };
}
