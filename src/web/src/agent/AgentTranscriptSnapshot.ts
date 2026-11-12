import type { AgentTranscriptEntry, AgentTranscriptSnapshot } from "./AgentPaneTranscriptTypes";
import { isResult } from "./AgentTranscriptLabels";

export const emptyTranscript: AgentTranscriptSnapshot = {
  agentTurnStartId: null,
  agentTurnStartIndex: null,
  currentThought: null,
  entries: [],
  entryIndexes: new Map(),
  latestResultIndex: null,
  pendingRowIndexes: [],
  sectionLabels: new Map(),
  turnActive: false,
};

/** `next`, reusing every part of `previous` it deep-equals; array items with an `id` match by id. */
export function share<T>(previous: unknown, next: T): T {
  if (Object.is(previous, next)) return previous as T;
  if (isObject(next) && "conversation" in next) return next;
  if (!isObject(previous) || !isObject(next) || Array.isArray(previous) !== Array.isArray(next)) {
    return next;
  }
  if (Array.isArray(next)) {
    const before = previous as unknown[];
    const byId = new Map(before.filter(hasId).map((item) => [item.id, item]));
    const shared = next.map((item, index) =>
      share(hasId(item) ? byId.get(item.id) : before[index], item),
    );
    return (sameItems(shared, before) ? before : shared) as T;
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
    next.currentThought === previous.currentThought &&
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
  const candidates = new Set([
    ...held.pendingRowIndexes.map((index) => held.entries[index]!.id),
    ...live.pendingRowIndexes.map((index) => live.entries[index]!.id),
  ]);
  const replacements = new Map<string, AgentTranscriptEntry | undefined>();
  const appended: AgentTranscriptEntry[] = [];
  const retained = new Set<number>();
  for (const id of candidates) {
    const oldIndex = held.entryIndexes.get(id);
    const liveIndex = live.entryIndexes.get(id);
    const current = liveIndex === undefined ? undefined : live.entries[liveIndex];
    const old = oldIndex === undefined ? undefined : held.entries[oldIndex];
    if (oldIndex === undefined) {
      if (current !== undefined) appended.push(current);
    } else {
      if (old?.conversation === undefined && old !== current) replacements.set(id, current);
      if (liveIndex !== undefined && live.pendingRowIndexes.includes(liveIndex))
        retained.add(oldIndex);
    }
  }
  let entries = held.entries;
  let entryIndexes = held.entryIndexes;
  if (replacements.size > 0 || appended.length > 0) {
    entries = [
      ...held.entries.flatMap((entry) =>
        replacements.has(entry.id)
          ? replacements.get(entry.id) === undefined
            ? []
            : [replacements.get(entry.id)!]
          : [entry],
      ),
      ...appended,
    ];
    entryIndexes = new Map(entries.map((entry, index) => [entry.id, index]));
    retained.clear();
    for (const index of live.pendingRowIndexes) {
      const placed = entryIndexes.get(live.entries[index]!.id);
      if (placed !== undefined) retained.add(placed);
    }
  }
  const pendingRowIndexes = [...retained].sort((a, b) => a - b);
  if (entries === held.entries && sameItems(pendingRowIndexes, held.pendingRowIndexes)) return held;
  return {
    ...held,
    entries,
    entryIndexes,
    pendingRowIndexes,
    agentTurnStartIndex:
      held.agentTurnStartId === null ? null : (entryIndexes.get(held.agentTurnStartId) ?? null),
    latestResultIndex: latestResultIndex(entries),
  };
}

/** Capture conversation snapshots once when reading is held. Only pending forms remain live. */
export function holdTranscript(snapshot: AgentTranscriptSnapshot): AgentTranscriptSnapshot {
  return {
    ...snapshot,
    entries: snapshot.entries.map((entry) => {
      const view = entry.conversation;
      if (view === undefined) return entry;
      const held = holdTranscript(view.transcript());
      return {
        ...entry,
        currentThought: held.currentThought,
        get asideEntries() {
          return withLivePending(held, view.transcript()).entries as AgentTranscriptEntry[];
        },
        get pendingEntries() {
          const current = withLivePending(held, view.transcript());
          return current.pendingRowIndexes.map((index) => current.entries[index]!);
        },
      };
    }),
  };
}

/** The last row's index, if it is a result. */
export function latestResultIndex(entries: readonly AgentTranscriptEntry[]): number | null {
  const last = entries.at(-1);
  return last !== undefined && isResult(last) ? entries.length - 1 : null;
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
