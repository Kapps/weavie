import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";

export function isPendingRequest(entry: AgentTranscriptEntry): boolean {
  return entry.kind === "request" && entry.status === "pending";
}

export function orderPendingRequests(source: readonly AgentTranscriptEntry[]): {
  entries: AgentTranscriptEntry[];
  pendingRowIndexes: number[];
} {
  const history: AgentTranscriptEntry[] = [];
  const pending: AgentTranscriptEntry[] = [];
  const retained = new Set<string>();
  for (const original of source) {
    let entry = original;
    if (entry.asideEntries !== undefined) {
      const aside = orderPendingRequests(entry.asideEntries);
      entry = { ...entry, asideEntries: aside.entries };
      if (aside.pendingRowIndexes.length > 0) retained.add(entry.id);
    }
    if (isPendingRequest(entry)) {
      pending.push(entry);
      retained.add(entry.id);
    } else {
      history.push(entry);
    }
  }
  const entries = [...history, ...pending];
  return {
    entries,
    pendingRowIndexes: entries.flatMap((entry, index) => (retained.has(entry.id) ? [index] : [])),
  };
}
