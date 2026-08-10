import type { AgentPaneUpdate } from "../bridge";
import { paneItemIdentity } from "./AgentPaneIdentity";

interface ItemBuffer {
  index: number;
  chunks: string[];
  latest: AgentPaneUpdate;
}

interface SlotState {
  buffers: Map<string, ItemBuffer>;
  changedIndexes: Set<number>;
  forceProject: boolean;
  indexes: Map<string, number>;
  messages: AgentPaneUpdate[];
  scheduled: boolean;
}

type Publish = (messages: AgentPaneUpdate[], changes: AgentPaneUpdate[]) => void;

export class AgentPaneAccumulator {
  private readonly slots = new Map<string, SlotState>();

  constructor(private readonly schedule: (callback: () => void) => void) {}

  ingest(slot: string, incoming: AgentPaneUpdate, publish: Publish): void {
    const state = this.state(slot);
    state.changedIndexes.add(this.store(state, incoming));
    this.scheduleFlush(state, slot, publish);
  }

  ingestBatch(slot: string, incoming: readonly AgentPaneUpdate[], publish: Publish): void {
    const state = this.state(slot);
    const forceProject = state.messages.length === 0;
    for (const message of incoming) {
      const index = this.store(state, message);
      if (!forceProject) {
        state.changedIndexes.add(index);
      }
    }
    state.forceProject ||= forceProject;
    this.scheduleFlush(state, slot, publish);
  }

  replace(slot: string, incoming: AgentPaneUpdate[], publish: Publish): void {
    this.slots.delete(slot);
    const state = this.state(slot);
    for (const message of incoming) {
      this.store(state, message);
    }
    this.materialize(state);
    state.changedIndexes.clear();
    publish(state.messages, []);
  }

  private store(state: SlotState, message: AgentPaneUpdate): number {
    if (message.type === "item-completed" && state.indexes.size === 0) {
      state.messages.push(message);
      return state.messages.length - 1;
    }
    const key = itemKey(message);
    // Every path only mutates state.messages (O(1)); a single per-frame flush publishes the snapshot. Publishing
    // synchronously here would rebuild the whole transcript on every message — O(N²) across a turn or a replay.
    if (key !== null && isDelta(message)) {
      return this.bufferDelta(state, key, message);
    } else if (key !== null && message.type === "item-started") {
      const index = state.indexes.get(key);
      if (index === undefined) {
        const next = state.messages.length;
        state.indexes.set(key, next);
        state.messages.push(message);
        return next;
      }
      state.messages[index] = message;
      return index;
    } else if (key !== null && message.type === "item-completed") {
      const index = state.indexes.get(key);
      state.buffers.delete(key);
      state.indexes.delete(key);
      if (index === undefined) {
        state.messages.push(message);
        return state.messages.length - 1;
      }
      state.messages[index] = message;
      return index;
    } else {
      state.messages.push(message);
      return state.messages.length - 1;
    }
  }

  reset(slot: string, publish: Publish): void {
    // Delete first: a flush queued before this reset re-fetches state by slot, finds none, and no-ops (see flush),
    // so it can never republish the cleared transcript.
    this.slots.delete(slot);
    publish([], []);
  }

  private bufferDelta(state: SlotState, key: string, message: AgentPaneUpdate): number {
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
    return buffer.index;
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
    const changes = state.forceProject
      ? []
      : [...state.changedIndexes].map((index) => state.messages[index]!);
    state.changedIndexes.clear();
    state.forceProject = false;
    publish(state.messages, changes);
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
        changedIndexes: new Set<number>(),
        forceProject: false,
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
  return paneItemIdentity(message);
}

function isDelta(message: AgentPaneUpdate): boolean {
  return (
    message.type === "agent-message-delta" ||
    message.type === "plan-delta" ||
    message.type === "command-output-delta"
  );
}
