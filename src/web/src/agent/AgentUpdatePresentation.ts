import { type Accessor, createMemo, createSignal } from "solid-js";
import type { AgentDefaults } from "../bridge";
import type { AgentTranscriptEntry, AgentTranscriptSnapshot } from "./AgentPaneTranscriptTypes";
import { computeSectionLabels, latestAgentTurnStartId } from "./AgentTranscriptLabels";
import { emptyTranscript, latestResultIndex, shareTranscript } from "./AgentTranscriptSnapshot";

export type ProgressDisplay = AgentDefaults["progressDisplay"];

/** Presentation owns visible indexes; activity updates continue to address the canonical snapshot. */
export function createUpdatePresentation(
  source: Accessor<AgentTranscriptSnapshot>,
  display: Accessor<ProgressDisplay>,
  expanded: Set<string>,
) {
  const [revision, invalidate] = createSignal(0);
  const transcript = createMemo<AgentTranscriptSnapshot>((previous) => {
    revision();
    return shareTranscript(previous, presentUpdates(source(), display(), expanded));
  }, emptyTranscript);
  return {
    transcript,
    setExpanded(id: string, open: boolean): boolean {
      const snapshot = transcript();
      const index = snapshot.entryIndexes.get(id);
      if (index === undefined || snapshot.entries[index]?.kind !== "updates") return false;
      if (open) expanded.add(id);
      else expanded.delete(id);
      invalidate((value) => value + 1);
      return true;
    },
  };
}

export function presentUpdates(
  source: AgentTranscriptSnapshot,
  display: ProgressDisplay,
  expanded: ReadonlySet<string>,
): AgentTranscriptSnapshot {
  if (display === "all") return source;
  const entries: AgentTranscriptEntry[] = [];
  const pending = new Set(source.pendingRowIndexes.map((index) => source.entries[index]!.id));
  let start = 0;
  let turn: string | undefined;
  let prompt: string | undefined;
  for (let index = 0; index <= source.entries.length; index++) {
    const entry = source.entries[index];
    const boundary = entry?.turnKey !== undefined && turn !== undefined && entry.turnKey !== turn;
    if (entry?.turnKey !== undefined) turn = entry.turnKey;
    if (entry !== undefined && entry.tone !== "user" && !boundary) continue;
    foldSpan(source.entries.slice(start, index), { prompt, pending, expanded }, entries);
    if (entry?.tone === "user") {
      entries.push(entry);
      prompt = entry.id;
    } else if (boundary) prompt = undefined;
    start = index + (entry?.tone === "user" ? 1 : 0);
  }
  const entryIndexes = new Map(entries.map((entry, index) => [entry.id, index]));
  const turnStart = latestAgentTurnStartId(entries);
  return {
    ...source,
    entries,
    entryIndexes,
    agentTurnStartId: turnStart,
    agentTurnStartIndex: turnStart === null ? null : (entryIndexes.get(turnStart) ?? null),
    latestResultIndex: latestResultIndex(entries),
    pendingRowIndexes: source.pendingRowIndexes.map(
      (index) => entryIndexes.get(source.entries[index]!.id)!,
    ),
    sectionLabels: computeSectionLabels(entries, source.turnActive),
  };
}

function foldSpan(
  source: readonly AgentTranscriptEntry[],
  options: {
    prompt: string | undefined;
    pending: ReadonlySet<string>;
    expanded: ReadonlySet<string>;
  },
  output: AgentTranscriptEntry[],
): void {
  const { prompt, pending, expanded } = options;
  const latest = source.findLastIndex(isReply);
  const folded = new Set(
    source.flatMap((entry, index) =>
      !pending.has(entry.id) && ((isReply(entry) && index !== latest) || isSettledWork(entry))
        ? [index]
        : [],
    ),
  );
  if (folded.size === 0) {
    for (const entry of source) output.push(entry);
    return;
  }
  const anchor = prompt ?? source.find((entry) => entry.kind !== "thought")!.id;
  const id = `updates:${anchor}`;
  const open = expanded.has(id);
  const first = folded.values().next().value!;
  // A history disclosure precedes the reply even when all of its work followed that reply.
  const position = latest < 0 ? first : Math.min(first, latest);
  source.forEach((entry, index) => {
    if (index === position) {
      output.push({
        id,
        kind: "updates",
        label: "Earlier updates",
        tone: "system",
        actionMessage: null,
        detailCount: folded.size,
        details: [],
        status: null,
        streaming: false,
        summary: null,
        text: null,
        updatesOpen: open,
      });
    }
    if (!folded.has(index) || open) output.push(entry);
  });
}

function isSettledWork(entry: AgentTranscriptEntry): boolean {
  switch (entry.kind) {
    case "activity":
      return entry.status === null;
    case "request":
      return entry.status !== "pending";
    case "subagent":
      return (
        !entry.asideActive &&
        !entry.conversation?.transcript().turnActive &&
        (entry.pendingEntries?.length ?? 0) === 0
      );
    default:
      return false;
  }
}

function isReply(entry: AgentTranscriptEntry): boolean {
  return entry.kind === "message" && entry.tone === "assistant";
}
