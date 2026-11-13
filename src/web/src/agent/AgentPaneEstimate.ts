import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { chartHeight, tableHeightEstimate } from "./agent-block-layout";

// Rendered-line metrics behind the size estimate. Prose wraps at the markdown measure
// (`.agent-markdown` is `max-width: 96ch`); fenced code and tool output are preformatted, so each
// source line is exactly one rendered line at the content role's smaller leading.
const proseColumns = 96;
/** Every agent image sits in a frame this tall, so loading one never moves the transcript. */
export const AGENT_IMAGE_HEIGHT = 240;
const proseLineHeight = 27;
const monoLineHeight = 18;
const estimates = new WeakMap<AgentTranscriptEntry, number>();

// Called for every unmeasured row on each measurement pass, so the per-entry result is memoized.
export function estimateEntrySize(entry: AgentTranscriptEntry | undefined): number {
  if (entry === undefined) {
    return 48;
  }
  if (entry.kind === "thought" && entry.thoughtCollapsed && !entry.thoughtOpen) return 29;
  if (entry.kind === "activity" || entry.kind === "updates") return 24;
  if (entry.block !== undefined)
    return entry.block.kind === "chart" ? chartHeight : tableHeightEstimate;
  if (entry.conversation !== undefined) return entry.conversation.estimatedHeight;
  const cached = estimates.get(entry);
  if (cached !== undefined) {
    return cached;
  }
  const prose =
    entry.kind === "thought" || (entry.kind === "message" && entry.tone === "assistant");
  if (entry.kind === "aside" || entry.kind === "dashboard") {
    const children =
      entry.kind === "dashboard" && entry.asideCollapsed ? [] : (entry.asideEntries ?? []);
    const size = 76 + children.reduce((sum, child) => sum + estimateEntrySize(child), 0);
    estimates.set(entry, size);
    return size;
  }
  const size =
    (prose ? 12 : 34) +
    (entry.summary === null ? 0 : wrappedHeight(entry.summary)) +
    (entry.text === null
      ? 0
      : prose
        ? markdownHeight(entry.text)
        : preformattedHeight(entry.text)) +
    (entry.detailCount > 0 || entry.actionMessage?.type === "user-selection" ? monoLineHeight : 0);
  estimates.set(entry, size);
  return size;
}

function markdownHeight(text: string): number {
  let height = 0;
  let fenced = false;
  for (const line of text.split("\n")) {
    if (line.startsWith("```")) {
      fenced = !fenced;
    } else {
      height += fenced
        ? monoLineHeight
        : wrappedHeight(line) + (line.match(/!\[[^\]]*\]\(/g)?.length ?? 0) * AGENT_IMAGE_HEIGHT;
    }
  }
  return height;
}

function preformattedHeight(text: string): number {
  let height = 0;
  for (const line of text.split("\n")) {
    height += Math.max(1, Math.ceil(line.length / proseColumns)) * monoLineHeight;
  }
  return height;
}

function wrappedHeight(line: string): number {
  return Math.max(1, Math.ceil(line.length / proseColumns)) * proseLineHeight;
}
