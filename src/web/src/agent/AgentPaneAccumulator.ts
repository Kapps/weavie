import type { AgentPaneHistoryFragment, AgentPaneUpdate, AgentPaneWireUpdate } from "../bridge";
import {
  type AgentPaneHistoryState,
  createAgentPaneHistoryState,
  type HistoryItemBuffer,
  isAgentPaneWireUpdate,
  mergeHistoryRecords,
} from "./AgentPaneHistoryAccumulator";
import { paneItemIdentity } from "./AgentPaneIdentity";

interface ItemBuffer extends HistoryItemBuffer {
  index: number;
}

interface SlotState {
  buffers: Map<string, ItemBuffer>;
  buffersByIndex: Map<number, ItemBuffer>;
  changedIndexes: Set<number>;
  forceProject: boolean;
  generation: number | null;
  history: AgentPaneHistoryState;
  historyInitialized: boolean;
  indexes: Map<string, number>;
  messages: AgentPaneUpdate[];
  revisions: Map<number, number>;
  scheduled: boolean;
}

type Publish = (messages: AgentPaneUpdate[], changes: AgentPaneUpdate[]) => void;

export class AgentPaneAccumulator {
  private readonly slots = new Map<string, SlotState>();

  constructor(private readonly schedule: (callback: () => void) => void) {}

  ingest(slot: string, incoming: AgentPaneUpdate, publish: Publish): void {
    const state = this.stateForUpdate(slot, incoming);
    if (state === null) {
      return;
    }
    state.changedIndexes.add(this.store(state, incoming, "append"));
    this.scheduleFlush(state, slot, publish);
  }

  ingestBatch(slot: string, incoming: readonly AgentPaneUpdate[], publish: Publish): void {
    let state = this.state(slot);
    let forceProject = state.messages.length === 0;
    for (const message of incoming) {
      const target = this.stateForUpdate(slot, message);
      if (target === null) {
        continue;
      }
      state = target;
      forceProject ||= state.messages.length === 0;
      const index = this.store(state, message, "append");
      if (!forceProject) {
        state.changedIndexes.add(index);
      }
    }
    state.forceProject ||= forceProject;
    this.scheduleFlush(state, slot, publish);
  }

  abandonHistory(slot: string): void {
    this.slots.get(slot)?.history.fragments.clear();
    this.slots.get(slot)?.history.records.clear();
  }

  mergeHistory(
    slot: string,
    generation: number,
    incoming: AgentPaneHistoryFragment[],
    completeRead: boolean,
    publish: Publish,
  ): void {
    const previous = this.slots.get(slot);
    if (
      previous !== undefined &&
      previous.generation !== null &&
      previous.generation > generation
    ) {
      return;
    }

    let source = previous;
    if (source === undefined || source.generation !== generation) {
      this.slots.delete(slot);
      source = this.state(slot);
      source.generation = generation;
    }

    const completed = mergeHistoryRecords(source.history, source.buffers, incoming);

    if (
      completeRead &&
      source.historyInitialized &&
      completed.length === 0 &&
      source.history.records.size === 0
    ) {
      return;
    }

    if ((source.historyInitialized || completed.length === 0) && !completeRead) {
      return;
    }

    this.materialize(source);
    const retained = source.messages;
    const completedOrdinals = new Set(completed.map((message) => message.ordinal));
    const byOrdinal = new Map<number, AgentPaneWireUpdate>();
    for (const message of [...source.history.records.values(), ...retained]) {
      if (isAgentPaneWireUpdate(message)) {
        const existing = byOrdinal.get(message.ordinal);
        if (existing === undefined || message.revision > existing.revision) {
          byOrdinal.set(message.ordinal, message);
        }
      }
    }

    const history = source.history;
    this.slots.delete(slot);
    const state = this.state(slot);
    state.generation = generation;
    state.history = history;
    state.historyInitialized = true;
    for (const message of [...byOrdinal.values()].sort(
      (left, right) => left.ordinal - right.ordinal,
    )) {
      state.revisions.set(message.ordinal, message.revision);
      if (!completedOrdinals.has(message.ordinal) && this.copyDeltaBuffer(source, state, message)) {
        continue;
      }
      this.store(state, message, "base");
    }
    this.materialize(state);
    if (completeRead) {
      state.history.records.clear();
    }
    state.changedIndexes.clear();
    state.forceProject = false;
    publish(state.messages, []);
  }

  private copyDeltaBuffer(
    source: SlotState,
    target: SlotState,
    message: AgentPaneWireUpdate,
  ): boolean {
    const key = itemKey(message);
    if (key === null) {
      return false;
    }
    const buffer = source.buffers.get(key);
    if (
      buffer === undefined ||
      !isAgentPaneWireUpdate(buffer.latest) ||
      buffer.latest.ordinal !== message.ordinal ||
      buffer.latest.revision !== message.revision
    ) {
      return false;
    }

    const index = target.messages.length;
    target.indexes.set(key, index);
    target.messages.push(message);
    const copied = {
      baseRevision: buffer.baseRevision,
      baseText: buffer.baseText,
      index,
      chunks: buffer.chunks.map((chunk) => ({ ...chunk })),
      latest: buffer.latest,
      text: buffer.text,
    };
    target.buffers.set(key, copied);
    target.buffersByIndex.set(index, copied);
    return true;
  }

  private store(state: SlotState, message: AgentPaneUpdate, deltaMode: "append" | "base"): number {
    if (message.type === "item-completed" && state.indexes.size === 0) {
      state.messages.push(message);
      return state.messages.length - 1;
    }
    const key = itemKey(message);
    // Every path only mutates state.messages (O(1)); a single per-frame flush publishes the snapshot. Publishing
    // synchronously here would rebuild the whole transcript on every message — O(N²) across a turn or a replay.
    if (key !== null && isDelta(message)) {
      return this.bufferDelta(state, key, message, deltaMode);
    } else if (key !== null && message.type === "item-started") {
      const index = state.indexes.get(key);
      if (index === undefined) {
        const next = state.messages.length;
        state.indexes.set(key, next);
        state.messages.push(message);
        return next;
      }
      state.buffers.delete(key);
      state.buffersByIndex.delete(index);
      state.messages[index] = message;
      return index;
    } else if (key !== null && message.type === "item-completed") {
      const index = state.indexes.get(key);
      state.buffers.delete(key);
      if (index !== undefined) {
        state.buffersByIndex.delete(index);
      }
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

  private bufferDelta(
    state: SlotState,
    key: string,
    message: AgentPaneUpdate,
    mode: "append" | "base",
  ): number {
    let buffer = state.buffers.get(key);
    if (buffer === undefined) {
      const existing = state.indexes.get(key);
      const index = existing ?? state.messages.length;
      buffer = {
        baseRevision: null,
        baseText: "",
        index,
        chunks: [],
        latest: message,
        text: "",
      };
      state.buffers.set(key, buffer);
      state.buffersByIndex.set(index, buffer);
      state.indexes.set(key, index);
      if (existing === undefined) {
        state.messages.push({ ...message, text: "" });
      }
    }
    buffer.latest = message;
    if (mode === "base") {
      buffer.baseRevision = isAgentPaneWireUpdate(message) ? message.revision : null;
      buffer.baseText = message.text ?? "";
      buffer.chunks = [];
      buffer.text = buffer.baseText;
    } else {
      const text = message.text ?? "";
      buffer.chunks.push({
        revision: isAgentPaneWireUpdate(message) ? message.revision : null,
        text,
      });
      buffer.text += text;
    }
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
    this.materializeIndexes(state, state.changedIndexes);
    const changes = state.forceProject
      ? []
      : [...state.changedIndexes].map((index) => state.messages[index]!);
    state.changedIndexes.clear();
    state.forceProject = false;
    publish(state.messages, changes);
  }

  private materialize(state: SlotState): void {
    this.materializeIndexes(state, state.buffersByIndex.keys());
  }

  private materializeIndexes(state: SlotState, indexes: Iterable<number>): void {
    for (const index of indexes) {
      const buffer = state.buffersByIndex.get(index);
      if (buffer === undefined) {
        continue;
      }
      state.messages[buffer.index] = {
        ...buffer.latest,
        text: buffer.text,
      };
      if (buffer.baseRevision !== null && isAgentPaneWireUpdate(buffer.latest)) {
        buffer.baseRevision = buffer.latest.revision;
        buffer.baseText = buffer.text;
        buffer.chunks = [];
      }
    }
  }

  private state(slot: string): SlotState {
    let state = this.slots.get(slot);
    if (state === undefined) {
      state = {
        buffers: new Map<string, ItemBuffer>(),
        buffersByIndex: new Map<number, ItemBuffer>(),
        changedIndexes: new Set<number>(),
        forceProject: false,
        generation: null,
        history: createAgentPaneHistoryState(),
        historyInitialized: false,
        indexes: new Map<string, number>(),
        messages: [],
        revisions: new Map<number, number>(),
        scheduled: false,
      };
      this.slots.set(slot, state);
    }
    return state;
  }

  private stateForUpdate(slot: string, incoming: AgentPaneUpdate): SlotState | null {
    let state = this.state(slot);
    if (!isAgentPaneWireUpdate(incoming)) {
      return state;
    }
    if (state.generation !== null && incoming.generation < state.generation) {
      return null;
    }
    if (state.generation !== incoming.generation) {
      this.slots.delete(slot);
      state = this.state(slot);
      state.generation = incoming.generation;
    }
    const revision = state.revisions.get(incoming.ordinal);
    if (revision !== undefined && incoming.revision <= revision) {
      return null;
    }
    state.revisions.set(incoming.ordinal, incoming.revision);
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
