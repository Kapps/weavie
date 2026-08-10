import type { AgentPaneHistoryFragment, AgentPaneUpdate, AgentPaneWireUpdate } from "../bridge";
import { paneItemIdentity } from "./AgentPaneIdentity";

interface ItemBuffer {
  baseRevision: number | null;
  baseText: string;
  index: number;
  chunks: Array<{ revision: number | null; text: string }>;
  latest: AgentPaneUpdate;
}

interface HistoryFragmentBuffer {
  parts: Map<number, string>;
  jsonLength: number;
}

interface SlotState {
  buffers: Map<string, ItemBuffer>;
  fragments: Map<string, HistoryFragmentBuffer>;
  generation: number | null;
  indexes: Map<string, number>;
  messages: AgentPaneUpdate[];
  revisions: Map<number, number>;
  scheduled: boolean;
}

type Publish = (messages: AgentPaneUpdate[]) => void;

export class AgentPaneAccumulator {
  private readonly slots = new Map<string, SlotState>();

  constructor(private readonly schedule: (callback: () => void) => void) {}

  ingest(slot: string, incoming: AgentPaneUpdate, publish: Publish): void {
    const state = this.stateForUpdate(slot, incoming);
    if (state === null) {
      return;
    }
    this.store(state, incoming, "append");
    this.scheduleFlush(state, slot, publish);
  }

  mergeHistory(
    slot: string,
    generation: number,
    incoming: AgentPaneHistoryFragment[],
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

    this.materialize(source);
    const retained = source.messages;
    const complete = incoming.flatMap((message) => {
      const assembled = this.assembleHistory(source, message);
      return assembled === null ? [] : [this.mergeCumulativeDelta(source, assembled)];
    });
    const completedOrdinals = new Set(complete.map((message) => message.ordinal));
    const byOrdinal = new Map<number, AgentPaneWireUpdate>();
    for (const message of [...complete, ...retained]) {
      if (isWireUpdate(message)) {
        const existing = byOrdinal.get(message.ordinal);
        if (existing === undefined || message.revision > existing.revision) {
          byOrdinal.set(message.ordinal, message);
        }
      }
    }

    const fragments = source.fragments;
    this.slots.delete(slot);
    const state = this.state(slot);
    state.fragments = fragments;
    state.generation = generation;
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
    publish([...state.messages]);
  }

  restartHistory(slot: string, generation: number): void {
    const state = this.slots.get(slot);
    if (state?.generation === generation) {
      state.fragments.clear();
    }
  }

  private assembleHistory(
    state: SlotState,
    message: AgentPaneHistoryFragment,
  ): AgentPaneWireUpdate | null {
    if (message.jsonOffset === 0 && message.json.length === message.jsonLength) {
      this.dropFragments(state, message.ordinal, message.revision);
      return parseHistoryRecord(message, message.json);
    }
    if (
      message.jsonOffset < 0 ||
      message.jsonLength < 0 ||
      message.jsonOffset + message.json.length > message.jsonLength
    ) {
      throw new Error("Received an invalid agent history record fragment.");
    }

    const key = `${message.ordinal}:${message.revision}`;
    let buffer = state.fragments.get(key);
    if (buffer === undefined) {
      buffer = {
        parts: new Map<number, string>(),
        jsonLength: message.jsonLength,
      };
      state.fragments.set(key, buffer);
    } else if (buffer.jsonLength !== message.jsonLength) {
      throw new Error("Received inconsistent agent history record fragments.");
    }
    const existing = buffer.parts.get(message.jsonOffset);
    if (existing !== undefined && existing !== message.json) {
      throw new Error("Received conflicting agent history record fragments.");
    }
    buffer.parts.set(message.jsonOffset, message.json);

    const parts = [...buffer.parts].sort(([left], [right]) => left - right);
    let offset = 0;
    for (const [start, part] of parts) {
      if (start !== offset) {
        return null;
      }
      offset += part.length;
    }
    if (offset !== buffer.jsonLength) {
      return null;
    }

    state.fragments.delete(key);
    this.dropFragments(state, message.ordinal, message.revision);
    return parseHistoryRecord(message, parts.map(([, part]) => part).join(""));
  }

  private dropFragments(state: SlotState, ordinal: number, throughRevision: number): void {
    for (const key of state.fragments.keys()) {
      const separator = key.indexOf(":");
      const fragmentOrdinal = Number(key.slice(0, separator));
      const fragmentRevision = Number(key.slice(separator + 1));
      if (fragmentOrdinal === ordinal && fragmentRevision <= throughRevision) {
        state.fragments.delete(key);
      }
    }
  }

  private mergeCumulativeDelta(
    state: SlotState,
    history: AgentPaneWireUpdate,
  ): AgentPaneWireUpdate {
    if (!isDelta(history)) {
      return history;
    }
    const buffer = [...state.buffers.values()].find(
      (candidate) => isWireUpdate(candidate.latest) && candidate.latest.ordinal === history.ordinal,
    );
    if (buffer === undefined) {
      return history;
    }

    let baseRevision = history.revision;
    let text = history.text ?? "";
    let template: AgentPaneWireUpdate = history;
    if (buffer.baseRevision !== null && buffer.baseRevision > baseRevision) {
      baseRevision = buffer.baseRevision;
      text = buffer.baseText;
      template = buffer.latest as AgentPaneWireUpdate;
    }
    const tail = buffer.chunks.filter(
      (chunk): chunk is { revision: number; text: string } =>
        chunk.revision !== null && chunk.revision > baseRevision,
    );
    if (tail.length === 0 && template === history) {
      return history;
    }
    if (tail.length > 0) {
      text += tail.map((chunk) => chunk.text).join("");
      template = buffer.latest as AgentPaneWireUpdate;
    }
    return { ...template, text, textOffset: 0, textLength: text.length };
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
      !isWireUpdate(buffer.latest) ||
      buffer.latest.ordinal !== message.ordinal ||
      buffer.latest.revision !== message.revision
    ) {
      return false;
    }

    const index = target.messages.length;
    target.indexes.set(key, index);
    target.messages.push(message);
    target.buffers.set(key, {
      baseRevision: buffer.baseRevision,
      baseText: buffer.baseText,
      index,
      chunks: buffer.chunks.map((chunk) => ({ ...chunk })),
      latest: buffer.latest,
    });
    return true;
  }

  private store(state: SlotState, message: AgentPaneUpdate, deltaMode: "append" | "base"): void {
    const key = itemKey(message);
    // Every path only mutates state.messages (O(1)); a single per-frame flush publishes the snapshot. Publishing
    // synchronously here would rebuild the whole transcript on every message — O(N²) across a turn or a replay.
    if (key !== null && isDelta(message)) {
      this.bufferDelta(state, key, message, deltaMode);
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

  private bufferDelta(
    state: SlotState,
    key: string,
    message: AgentPaneUpdate,
    mode: "append" | "base",
  ): void {
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
      };
      state.buffers.set(key, buffer);
      state.indexes.set(key, index);
      if (existing === undefined) {
        state.messages.push({ ...message, text: "" });
      }
    }
    buffer.latest = message;
    if (mode === "base") {
      buffer.baseRevision = isWireUpdate(message) ? message.revision : null;
      buffer.baseText = message.text ?? "";
      buffer.chunks = [];
    } else {
      buffer.chunks.push({
        revision: isWireUpdate(message) ? message.revision : null,
        text: message.text ?? "",
      });
    }
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
      state.messages[buffer.index] = {
        ...buffer.latest,
        text: buffer.baseText + buffer.chunks.map((chunk) => chunk.text).join(""),
      };
    }
  }

  private state(slot: string): SlotState {
    let state = this.slots.get(slot);
    if (state === undefined) {
      state = {
        buffers: new Map<string, ItemBuffer>(),
        fragments: new Map<string, HistoryFragmentBuffer>(),
        generation: null,
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
    if (!isWireUpdate(incoming)) {
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

function isWireUpdate(message: AgentPaneUpdate): message is AgentPaneWireUpdate {
  return (
    "generation" in message &&
    Number.isInteger(message.generation) &&
    "ordinal" in message &&
    Number.isInteger(message.ordinal) &&
    "revision" in message &&
    Number.isInteger(message.revision) &&
    "textOffset" in message &&
    Number.isInteger(message.textOffset) &&
    "textLength" in message &&
    Number.isInteger(message.textLength)
  );
}

function parseHistoryRecord(fragment: AgentPaneHistoryFragment, json: string): AgentPaneWireUpdate {
  const parsed: unknown = JSON.parse(json);
  const candidate = parsed as AgentPaneUpdate;
  if (
    typeof parsed !== "object" ||
    parsed === null ||
    !isWireUpdate(candidate) ||
    candidate.generation !== fragment.generation ||
    candidate.ordinal !== fragment.ordinal ||
    candidate.revision !== fragment.revision ||
    candidate.textOffset !== 0 ||
    candidate.textLength !== (candidate.text?.length ?? 0)
  ) {
    throw new Error("Received an invalid serialized agent history record.");
  }
  return candidate;
}
