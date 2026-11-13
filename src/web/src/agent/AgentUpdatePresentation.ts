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
  let start = 0;
  let turn: string | undefined;
  for (let index = 0; index <= source.entries.length; index++) {
    const entry = source.entries[index];
    const boundary = entry?.turnKey !== undefined && turn !== undefined && entry.turnKey !== turn;
    if (entry?.turnKey !== undefined) turn = entry.turnKey;
    if (entry !== undefined && entry.tone !== "user" && !boundary) continue;
    foldSpan(source.entries.slice(start, index), expanded, entries);
    if (entry?.tone === "user") entries.push(entry);
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
  expanded: ReadonlySet<string>,
  output: AgentTranscriptEntry[],
): void {
  const replies = source.flatMap((entry, index) => (isReply(entry) ? [index] : []));
  if (replies.length < 2) {
    for (const entry of source) output.push(entry);
    return;
  }
  const latest = replies.at(-1)!;
  const id = `updates:${source[replies[0]!]!.id}`;
  const open = expanded.has(id);
  let inserted = false;
  source.forEach((entry, index) => {
    const hidden =
      index < latest &&
      (isReply(entry) ||
        (entry.kind === "activity" && entry.status === null && entry.tone === "activity"));
    if (hidden && !inserted) {
      output.push({
        id,
        kind: "updates",
        label: "Earlier updates",
        tone: "system",
        actionMessage: null,
        detailCount: replies.length - 1,
        details: [],
        status: null,
        streaming: false,
        summary: null,
        text: null,
        updatesOpen: open,
      });
      inserted = true;
    }
    if (!hidden || open) output.push(entry);
  });
}

function isReply(entry: AgentTranscriptEntry): boolean {
  return entry.kind === "message" && entry.tone === "assistant";
}
