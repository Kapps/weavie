import type { AgentPaneUpdate } from "../bridge";
import { paneItemIdentity } from "./AgentPaneIdentity";

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
    const message = received(incoming);
    const state = this.state(slot);
    this.store(state, message);
    this.scheduleFlush(state, slot, publish);
  }

  replace(slot: string, incoming: AgentPaneUpdate[], publish: Publish): void {
    this.slots.delete(slot);
    const state = this.state(slot);
    for (const message of incoming) {
      this.store(state, received(message));
    }
    this.materialize(state);
    publish([...state.messages]);
  }

  private store(state: SlotState, message: AgentPaneUpdate): void {
    const key = itemKey(message);
    // Every path only mutates state.messages (O(1)); a single per-frame flush publishes the snapshot. Publishing
    // synchronously here would rebuild the whole transcript on every message — O(N²) across a turn or a replay.
    if (key !== null && isDelta(message)) {
      this.bufferDelta(state, key, message);
    } else if (key !== null && message.type === "item-started") {
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
      state.indexes.delete(key);
      if (index === undefined) {
        state.messages.push(message);
      } else {
        state.messages[index] = message;
      }
    } else {
      state.messages.push(message);
    }
  }

  reset(slot: string, publish: Publish): void {
    // Delete first: a flush queued before this reset re-fetches state by slot, finds none, and no-ops (see flush),
    // so it can never republish the cleared transcript.
    this.slots.delete(slot);
    publish([]);
  }

  private bufferDelta(state: SlotState, key: string, message: AgentPaneUpdate): void {
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
  }

  private scheduleFlush(state: SlotState, slot: string, publish: Publish): void {
    if (state.scheduled) {
      return;
    }
    state.scheduled = true;
    this.schedule(() => this.flush(slot, publish));
  }

  private flush(slot: string, publish: Publish): void {
    const state = this.slots.get(slot);
    if (state === undefined || !state.scheduled) {
      return;
    }
    state.scheduled = false;
    this.materialize(state);
    publish([...state.messages]);
  }

  private materialize(state: SlotState): void {
    for (const buffer of state.buffers.values()) {
      state.messages[buffer.index] = { ...buffer.latest, text: buffer.chunks.join("") };
    }
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

function received(message: AgentPaneUpdate): AgentPaneUpdate {
  return message.type === "turn-started" && message.receivedAt === undefined
    ? { ...message, receivedAt: Date.now() }
    : message;
}

function itemKey(message: AgentPaneUpdate): string | null {
  return paneItemIdentity(message);
}

function isDelta(message: AgentPaneUpdate): boolean {
  return (
    message.type === "agent-message-delta" ||
    message.type === "plan-delta" ||
    message.type === "command-output-delta"
  );
}
