import type { AgentPaneUpdate } from "../bridge";

interface ItemBuffer {
  index: number;
  chunks: string[];
  latest: AgentPaneUpdate;
}

interface SlotState {
  buffers: Map<string, ItemBuffer>;
  indexes: Map<string, number>;
  messages: AgentPaneUpdate[];
  scheduled: boolean;
}

type Publish = (messages: AgentPaneUpdate[]) => void;

export class AgentPaneAccumulator {
  private readonly slots = new Map<string, SlotState>();

  constructor(private readonly schedule: (callback: () => void) => void) {}

  ingest(slot: string, incoming: AgentPaneUpdate, publish: Publish): void {
    // Anchor the turn timer to when the turn actually began: stamp turn starts on arrival (for every slot,
    // focused or not) so the elapsed clock reflects real duration and never restarts on a session switch.
    // A page reload / bridge reconnect replays turn-started without receivedAt, so the clock re-baselines
    // then — the deliberate cost of a web-clock anchor, which avoids host/browser skew on remote sessions.
    const message =
      incoming.type === "turn-started" && incoming.receivedAt === undefined
        ? { ...incoming, receivedAt: Date.now() }
        : incoming;
    const state = this.state(slot);
    const key = itemKey(message);
    if (key !== null && isDelta(message)) {
      this.bufferDelta(slot, state, key, message, publish);
      return;
    }

    if (key !== null && message.type === "item-started") {
      const index = state.indexes.get(key);
      if (index === undefined) {
        state.indexes.set(key, state.messages.length);
        state.messages.push(message);
      } else {
        state.messages[index] = message;
      }
    } else if (key !== null && message.type === "item-completed") {
      const index = state.indexes.get(key);
      state.buffers.delete(key);
      if (state.buffers.size === 0) {
        state.scheduled = false;
      }
      state.indexes.delete(key);
      if (index === undefined) {
        state.messages.push(message);
      } else {
        state.messages[index] = message;
      }
    } else {
      state.messages.push(message);
    }
    publish([...state.messages]);
  }

  reset(slot: string, publish: Publish): void {
    this.slots.delete(slot);
    publish([]);
  }

  private bufferDelta(
    slot: string,
    state: SlotState,
    key: string,
    message: AgentPaneUpdate,
    publish: Publish,
  ): void {
    let buffer = state.buffers.get(key);
    if (buffer === undefined) {
      const existing = state.indexes.get(key);
      const index = existing ?? state.messages.length;
      buffer = { index, chunks: [], latest: message };
      state.buffers.set(key, buffer);
      state.indexes.set(key, index);
      if (existing === undefined) {
        state.messages.push({ ...message, text: "" });
      }
    }
    buffer.latest = message;
    buffer.chunks.push(message.text ?? "");
    if (!state.scheduled) {
      state.scheduled = true;
      this.schedule(() => this.flush(slot, publish));
    }
  }

  private flush(slot: string, publish: Publish): void {
    const state = this.slots.get(slot);
    if (state === undefined || !state.scheduled) {
      return;
    }
    state.scheduled = false;
    for (const buffer of state.buffers.values()) {
      state.messages[buffer.index] = { ...buffer.latest, text: buffer.chunks.join("") };
    }
    publish([...state.messages]);
  }

  private state(slot: string): SlotState {
    let state = this.slots.get(slot);
    if (state === undefined) {
      state = {
        buffers: new Map<string, ItemBuffer>(),
        indexes: new Map<string, number>(),
        messages: [],
        scheduled: false,
      };
      this.slots.set(slot, state);
    }
    return state;
  }
}

function itemKey(message: AgentPaneUpdate): string | null {
  return message.itemId == null || message.itemId.length === 0
    ? null
    : `${message.turnId ?? "session"}:${message.itemId}`;
}

function isDelta(message: AgentPaneUpdate): boolean {
  return (
    message.type === "agent-message-delta" ||
    message.type === "plan-delta" ||
    message.type === "command-output-delta"
  );
}
