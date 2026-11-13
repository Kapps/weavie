import type { AgentDefaults } from "../bridge";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";

export type ThoughtDisplay = AgentDefaults["thoughtDisplay"];

/** Only reasoning changes presentation; tools and ordinary replies retain their transcript rows. */
export function presentThoughts(
  entries: AgentTranscriptEntry[],
  display: ThoughtDisplay,
  expanded: ReadonlySet<string>,
): AgentTranscriptEntry[] {
  if (display !== "collapsed") return entries;
  return entries.map((entry) =>
    entry.kind === "thought"
      ? { ...entry, thoughtCollapsed: true, thoughtOpen: expanded.has(entry.id) }
      : entry,
  );
}
