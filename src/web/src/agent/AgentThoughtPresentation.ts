import type { AgentDefaults } from "../bridge";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";

export type ThoughtDisplay = AgentDefaults["thoughtDisplay"];

/** Folds only earlier exchanges; tools and ordinary replies keep their own transcript rows. */
export function presentThoughts(
  entries: AgentTranscriptEntry[],
  display: ThoughtDisplay,
  expanded: ReadonlySet<string>,
): AgentTranscriptEntry[] {
  if (display === "all") return entries;
  if (display === "collapsed")
    return entries.map((entry) =>
      entry.kind === "thought"
        ? { ...entry, thoughtCollapsed: true, thoughtOpen: expanded.has(entry.id) }
        : entry,
    );

  const latest = entries.findLastIndex((entry) => entry.exchangeStart);
  const output: AgentTranscriptEntry[] = [];
  let group: AgentTranscriptEntry | null = null;
  let thoughts: AgentTranscriptEntry[] = [];
  for (const [index, entry] of entries.entries()) {
    if (entry.exchangeStart) group = null;
    if (entry.kind !== "thought" || index > latest) {
      output.push(entry);
      continue;
    }
    if (group === null) {
      thoughts = [];
      const id = `thought-history:${entry.id}`;
      group = {
        ...entry,
        id,
        kind: "thought-history",
        label: "Earlier thinking",
        text: null,
        streaming: false,
        thoughts,
        thoughtOpen: expanded.has(id),
      };
      output.push(group);
    }
    thoughts.push(entry);
  }
  return output;
}
