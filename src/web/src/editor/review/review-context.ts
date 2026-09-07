import { monaco } from "../monaco-setup";
import type { DiffMarkers } from "./diff-markers";

// Unchanged lines kept either side of a change, matching the file-review reading distance.
const CONTEXT_LINES = 3;

// A section reserves its height from its line counts until the real editor reports one, so the virtualizer's
// offsets don't collapse while models resolve. Nominal metrics — the editor overwrites them on first measure.
const NOMINAL_LINE_HEIGHT = 19;
const EDITOR_PADDING = 12;

/** The height a section's editor reserves before it mounts: its changed lines plus the context around them. */
export function estimatedEditorHeight(added: number, removed: number): number {
  return (added + removed + CONTEXT_LINES * 2) * NOMINAL_LINE_HEIGHT + EDITOR_PADDING;
}

/**
 * Hides every line more than `CONTEXT_LINES` from a change (bright or accepted), and marks the first line after
 * each collapsed stretch so a gap reads as a gap. A timed-out diff (null markers) collapses nothing.
 */
export function collapseUnchanged(
  markers: DiffMarkers | null,
  lineCount: number,
): { hidden: monaco.IRange[]; gapMarkers: monaco.editor.IModelDeltaDecoration[] } {
  const spans = markers === null ? [] : changedSpans(markers);
  if (spans.length === 0) {
    return { hidden: [], gapMarkers: [] };
  }
  // A pure deletion's span is empty (end < start) and its ghost hangs off the line above, so pad both edges.
  const padded = spans
    .map((span) => ({
      start: Math.max(1, Math.min(span.start, span.end + 1) - CONTEXT_LINES),
      end: Math.min(lineCount, Math.max(span.end, span.start - 1) + CONTEXT_LINES),
    }))
    .sort((a, b) => a.start - b.start);
  const shown: { start: number; end: number }[] = [];
  for (const span of padded) {
    const last = shown.at(-1);
    if (last !== undefined && span.start <= last.end + 1) {
      last.end = Math.max(last.end, span.end);
    } else {
      shown.push({ ...span });
    }
  }

  const hidden: monaco.IRange[] = [];
  const gapMarkers: monaco.editor.IModelDeltaDecoration[] = [];
  let line = 1;
  for (const span of shown) {
    if (span.start > line) {
      hidden.push(new monaco.Range(line, 1, span.start - 1, 1));
      gapMarkers.push({
        range: new monaco.Range(span.start, 1, span.start, 1),
        options: { isWholeLine: true, className: "weavie-review-gap" },
      });
    }
    line = span.end + 1;
  }
  if (line <= lineCount) {
    hidden.push(new monaco.Range(line, 1, lineCount, 1));
  }
  return { hidden, gapMarkers };
}

// Every changed line range in live-model coordinates: the bright pending hunks plus the faded accepted ones.
function changedSpans(markers: DiffMarkers): { start: number; end: number }[] {
  return [
    ...markers.hunks.map((hunk) => ({
      start: hunk.currentStart,
      end: hunk.currentEndExclusive - 1,
    })),
    ...markers.acceptedHunks.map((hunk) => ({
      start: hunk.anchorLine,
      end: hunk.anchorLine + (hunk.reviewEndExclusive - hunk.reviewStart) - 1,
    })),
  ];
}
