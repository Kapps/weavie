import type { AgentPaneUpdate, AgentPaneWireUpdate } from "../bridge";
import { isAgentPaneDelta } from "./AgentPaneDelta";
import { paneItemIdentity } from "./AgentPaneIdentity";

export interface HistoryItemBuffer {
  baseRevision: number | null;
  baseText: string;
  chunks: Array<{ revision: number | null; text: string }>;
  latest: AgentPaneUpdate;
  text: string;
}

export interface AgentPaneHistoryState {
  records: Map<number, AgentPaneWireUpdate>;
}

export function createAgentPaneHistoryState(): AgentPaneHistoryState {
  return {
    records: new Map<number, AgentPaneWireUpdate>(),
  };
}

export function mergeHistoryRecords(
  state: AgentPaneHistoryState,
  buffers: ReadonlyMap<string, HistoryItemBuffer>,
  incoming: AgentPaneWireUpdate[],
): AgentPaneWireUpdate[] {
  const completed = incoming.map((message) => mergeCumulativeDelta(buffers, message));
  for (const message of completed) {
    const existing = state.records.get(message.ordinal);
    if (existing === undefined || message.revision > existing.revision) {
      state.records.set(message.ordinal, message);
    }
  }
  return completed;
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
