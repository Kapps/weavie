import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";

// Rendered-line metrics behind the size estimate. Prose wraps at the markdown measure
// (`.agent-markdown` is `max-width: 96ch`); fenced code and tool output are preformatted, so each
// source line is exactly one rendered line at the content role's smaller leading.
const proseColumns = 96;
const proseLineHeight = 27;
const monoLineHeight = 18;
const estimates = new WeakMap<AgentTranscriptEntry, number>();

// Called for every unmeasured row on each measurement pass, so the per-entry result is memoized.
export function estimateEntrySize(entry: AgentTranscriptEntry | undefined): number {
  if (entry === undefined) {
    return 48;
  }
  const cached = estimates.get(entry);
  if (cached !== undefined) {
    return cached;
  }
  const prose = entry.kind === "message" && entry.tone === "assistant";
  const size =
    (prose ? 12 : 34) +
    (entry.summary === null ? 0 : wrappedHeight(entry.summary)) +
    (entry.text === null
      ? 0
      : prose
        ? markdownHeight(entry.text)
        : preformattedHeight(entry.text)) +
    (entry.detailCount > 0 ? monoLineHeight : 0);
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
      height += fenced ? monoLineHeight : wrappedHeight(line);
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
