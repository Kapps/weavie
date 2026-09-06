import type { AgentPaneHistoryFragment, AgentPaneUpdate, AgentPaneWireUpdate } from "../bridge";
import { isAgentPaneDelta } from "./AgentPaneDelta";
import { paneItemIdentity } from "./AgentPaneIdentity";

export interface HistoryItemBuffer {
  baseRevision: number | null;
  baseText: string;
  chunks: Array<{ revision: number | null; text: string }>;
  latest: AgentPaneUpdate;
  text: string;
}

interface FragmentBuffer {
  parts: Map<number, string>;
  jsonLength: number;
  receivedLength: number;
}

export interface AgentPaneHistoryState {
  fragments: Map<string, FragmentBuffer>;
  records: Map<number, AgentPaneWireUpdate>;
}

export function createAgentPaneHistoryState(): AgentPaneHistoryState {
  return {
    fragments: new Map<string, FragmentBuffer>(),
    records: new Map<number, AgentPaneWireUpdate>(),
  };
}

export function mergeHistoryRecords(
  state: AgentPaneHistoryState,
  buffers: ReadonlyMap<string, HistoryItemBuffer>,
  incoming: AgentPaneHistoryFragment[],
): AgentPaneWireUpdate[] {
  const completed = incoming.flatMap((fragment) => {
    const assembled = assemble(state, fragment);
    return assembled === null ? [] : [mergeCumulativeDelta(buffers, assembled)];
  });
  for (const message of completed) {
    const existing = state.records.get(message.ordinal);
    if (existing === undefined || message.revision > existing.revision) {
      state.records.set(message.ordinal, message);
    }
  }
  return completed;
}

function assemble(
  state: AgentPaneHistoryState,
  message: AgentPaneHistoryFragment,
): AgentPaneWireUpdate | null {
  if (message.jsonOffset === 0 && message.json.length === message.jsonLength) {
    dropFragments(state, message.ordinal, message.revision);
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
      receivedLength: 0,
    };
    state.fragments.set(key, buffer);
  } else if (buffer.jsonLength !== message.jsonLength) {
    throw new Error("Received inconsistent agent history record fragments.");
  }
  const existing = buffer.parts.get(message.jsonOffset);
  if (existing !== undefined && existing !== message.json) {
    throw new Error("Received conflicting agent history record fragments.");
  }
  if (existing === undefined) {
    buffer.parts.set(message.jsonOffset, message.json);
    buffer.receivedLength += message.json.length;
  }
  if (buffer.receivedLength < buffer.jsonLength) {
    return null;
  }
  if (buffer.receivedLength > buffer.jsonLength) {
    throw new Error("Received overlapping agent history record fragments.");
  }

  const parts = [...buffer.parts].sort(([left], [right]) => left - right);
  let offset = 0;
  for (const [start, part] of parts) {
    if (start !== offset) {
      throw new Error("Received overlapping agent history record fragments.");
    }
    offset += part.length;
  }
  if (offset !== buffer.jsonLength) {
    throw new Error("Received incomplete agent history record fragments.");
  }

  state.fragments.delete(key);
  dropFragments(state, message.ordinal, message.revision);
  return parseHistoryRecord(message, parts.map(([, part]) => part).join(""));
}

function dropFragments(
  state: AgentPaneHistoryState,
  ordinal: number,
  throughRevision: number,
): void {
  for (const key of state.fragments.keys()) {
    const separator = key.indexOf(":");
    const fragmentOrdinal = Number(key.slice(0, separator));
    const fragmentRevision = Number(key.slice(separator + 1));
    if (fragmentOrdinal === ordinal && fragmentRevision <= throughRevision) {
      state.fragments.delete(key);
    }
  }
}

function mergeCumulativeDelta(
  buffers: ReadonlyMap<string, HistoryItemBuffer>,
  history: AgentPaneWireUpdate,
): AgentPaneWireUpdate {
  if (!isAgentPaneDelta(history)) {
    return history;
  }
  const key = paneItemIdentity(history);
  const buffer = key === null ? undefined : buffers.get(key);
  if (buffer === undefined) {
    return history;
  }

  let baseRevision = history.revision;
  let baseText = history.text ?? "";
  let template: AgentPaneWireUpdate = history;
  if (buffer.baseRevision !== null && buffer.baseRevision > baseRevision) {
    baseRevision = buffer.baseRevision;
    baseText = buffer.baseText;
    template = buffer.latest as AgentPaneWireUpdate;
  }
  const tail = buffer.chunks.filter(
    (chunk): chunk is { revision: number; text: string } =>
      chunk.revision !== null && chunk.revision > baseRevision,
  );
  const text = baseText + tail.map((chunk) => chunk.text).join("");
  if (tail.length > 0) {
    template = buffer.latest as AgentPaneWireUpdate;
  }
  buffer.baseRevision = baseRevision;
  buffer.baseText = baseText;
  buffer.chunks = tail;
  buffer.text = text;
  return { ...template, text, textOffset: 0, textLength: text.length };
}

export function isAgentPaneWireUpdate(message: AgentPaneUpdate): message is AgentPaneWireUpdate {
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
    !isAgentPaneWireUpdate(candidate) ||
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
