import type { AgentTranscriptEntry, AgentTranscriptSnapshot } from "./AgentPaneTranscriptTypes";
import { isPendingRequest } from "./AgentPendingRequests";
import { isResult } from "./AgentTranscriptLabels";

export const emptyTranscript: AgentTranscriptSnapshot = {
  agentTurnStartId: null,
  agentTurnStartIndex: null,
  entries: [],
  latestResultIndex: null,
  pendingRowIndexes: [],
  sectionLabels: new Map(),
  turnActive: false,
};

/** `next`, reusing every part of `previous` it deep-equals; array items with an `id` match by id. */
export function share<T>(previous: unknown, next: T): T {
  if (Object.is(previous, next)) return previous as T;
  if (!isObject(previous) || !isObject(next) || Array.isArray(previous) !== Array.isArray(next)) {
    return next;
  }
  if (Array.isArray(next)) {
    const before = previous as unknown[];
    const byId = new Map(before.filter(hasId).map((item) => [item.id, item]));
    const shared = next.map((item, index) =>
      share(hasId(item) ? byId.get(item.id) : before[index], item),
    );
    return (
      shared.length === before.length && shared.every((item, index) => item === before[index])
        ? before
        : shared
    ) as T;
  }
  const before = previous as Record<string, unknown>;
  const keys = Object.keys(next);
  let same = keys.length === Object.keys(before).length;
  const shared: Record<string, unknown> = {};
  for (const key of keys) {
    shared[key] = share(before[key], (next as Record<string, unknown>)[key]);
    same &&= key in before && shared[key] === before[key];
  }
  return (same ? before : shared) as T;
}

/** `next` sharing `previous`'s unchanged entries, or `previous` itself when nothing changed. */
export function shareTranscript(
  previous: AgentTranscriptSnapshot,
  next: AgentTranscriptSnapshot,
): AgentTranscriptSnapshot {
  const entries = share(previous.entries, next.entries);
  const unchanged =
    entries === previous.entries &&
    next.agentTurnStartId === previous.agentTurnStartId &&
    next.agentTurnStartIndex === previous.agentTurnStartIndex &&
    next.latestResultIndex === previous.latestResultIndex &&
    next.turnActive === previous.turnActive &&
    sameItems(next.pendingRowIndexes, previous.pendingRowIndexes) &&
    next.sectionLabels.size === previous.sectionLabels.size &&
    [...next.sectionLabels].every(([id, label]) => previous.sectionLabels.get(id) === label);
  return unchanged ? previous : { ...next, entries };
}

/**
 * A held snapshot whose request rows are live: a row holding a request pending in either view is replaced in place
 * by the live row with its id, or dropped once live no longer has it, and live request rows the hold lacks follow.
 */
export function withLivePending(
  held: AgentTranscriptSnapshot,
  live: AgentTranscriptSnapshot,
): AgentTranscriptSnapshot {
  const liveById = new Map(live.entries.map((entry) => [entry.id, entry]));
  const placed = new Set<string>();
  const entries = held.entries.flatMap((entry) => {
    const current = liveById.get(entry.id);
    if (!holdsRequest(entry) && (current === undefined || !holdsRequest(current))) return [entry];
    placed.add(entry.id);
    return current === undefined ? [] : [current];
  });
  for (const entry of live.entries) {
    if (holdsRequest(entry) && !placed.has(entry.id)) entries.push(entry);
  }
  if (sameItems(entries, held.entries)) return held;
  const turnStart = entries.findIndex((entry) => entry.id === held.agentTurnStartId);
  const last = entries.at(-1);
  return {
    ...held,
    agentTurnStartIndex: turnStart < 0 ? null : turnStart,
    entries,
    latestResultIndex: last !== undefined && isResult(last) ? entries.length - 1 : null,
    pendingRowIndexes: entries.flatMap((entry, index) => (holdsRequest(entry) ? [index] : [])),
  };
}

// A pending request, or a card nesting one.
function holdsRequest(entry: AgentTranscriptEntry): boolean {
  return isPendingRequest(entry) || entry.asideEntries?.some(holdsRequest) === true;
}

function sameItems<T>(left: readonly T[], right: readonly T[]): boolean {
  return left.length === right.length && left.every((item, index) => item === right[index]);
}

function isObject(value: unknown): value is object {
  return typeof value === "object" && value !== null;
}

function hasId(value: unknown): value is { id: string } {
  return isObject(value) && typeof (value as { id?: unknown }).id === "string";
}
