import { computeTextDiffLines, type DiffLineChange, splitDiffLines } from "./diff-computation";

const CONTEXT_LINES = 3;

export type ReviewDiffRowKind = "context" | "added" | "removed";

export interface ReviewDiffRow {
  kind: ReviewDiffRowKind;
  oldLine: number | null;
  newLine: number | null;
  text: string;
}

export interface ReviewDiffHunk {
  header: string;
  rows: ReviewDiffRow[];
  newLine: number;
}

export interface ReviewDiffPatch {
  hunks: ReviewDiffHunk[];
  timedOut: boolean;
  changes: readonly DiffLineChange[];
}

interface ChangeRange {
  oldStart: number;
  oldEnd: number;
  newStart: number;
  newEnd: number;
}

/** Builds GitHub-style unified hunks with line numbers and three lines of surrounding context. */
export function buildReviewDiffPatch(originalText: string, modifiedText: string): ReviewDiffPatch {
  if (originalText === modifiedText) {
    return { hunks: [], timedOut: false, changes: [] };
  }
  const changes = computeTextDiffLines(originalText, modifiedText);
  if (changes === null) {
    return { hunks: [], timedOut: true, changes: [] };
  }
  if (changes.length === 0) {
    return { hunks: [], timedOut: false, changes };
  }

  const original = splitDiffLines(originalText);
  const modified = splitDiffLines(modifiedText);
  const ranges: ChangeRange[] = changes.map((change) => ({
    oldStart: change.original.startLineNumber,
    oldEnd: change.original.endLineNumberExclusive,
    newStart: change.modified.startLineNumber,
    newEnd: change.modified.endLineNumberExclusive,
  }));
  return {
    hunks: groupNearbyChanges(ranges).map((group) => buildHunk(group, original, modified)),
    timedOut: false,
    changes,
  };
}

function groupNearbyChanges(changes: ChangeRange[]): ChangeRange[][] {
  const groups: ChangeRange[][] = [];
  for (const change of changes) {
    const group = groups.at(-1);
    const previous = group?.at(-1);
    const oldGap =
      previous === undefined ? Number.POSITIVE_INFINITY : change.oldStart - previous.oldEnd;
    const newGap =
      previous === undefined ? Number.POSITIVE_INFINITY : change.newStart - previous.newEnd;
    if (group === undefined || oldGap > CONTEXT_LINES * 2 || newGap > CONTEXT_LINES * 2) {
      groups.push([change]);
    } else {
      group.push(change);
    }
  }
  return groups;
}

function buildHunk(changes: ChangeRange[], original: string[], modified: string[]): ReviewDiffHunk {
  const first = changes[0]!;
  const last = changes.at(-1)!;
  const oldStart = Math.max(1, first.oldStart - CONTEXT_LINES);
  const newStart = Math.max(1, first.newStart - CONTEXT_LINES);
  const oldLimit = Math.min(original.length + 1, last.oldEnd + CONTEXT_LINES);
  const newLimit = Math.min(modified.length + 1, last.newEnd + CONTEXT_LINES);
  const rows: ReviewDiffRow[] = [];
  let oldLine = oldStart;
  let newLine = newStart;

  for (const change of changes) {
    while (oldLine < change.oldStart && newLine < change.newStart) {
      rows.push({ kind: "context", oldLine, newLine, text: original[oldLine - 1] ?? "" });
      oldLine += 1;
      newLine += 1;
    }
    while (oldLine < change.oldEnd) {
      rows.push({ kind: "removed", oldLine, newLine: null, text: original[oldLine - 1] ?? "" });
      oldLine += 1;
    }
    while (newLine < change.newEnd) {
      rows.push({ kind: "added", oldLine: null, newLine, text: modified[newLine - 1] ?? "" });
      newLine += 1;
    }
  }
  while (oldLine < oldLimit && newLine < newLimit) {
    rows.push({ kind: "context", oldLine, newLine, text: original[oldLine - 1] ?? "" });
    oldLine += 1;
    newLine += 1;
  }

  return {
    header: `@@ -${rangeLabel(oldStart, oldLine - oldStart)} +${rangeLabel(newStart, newLine - newStart)} @@`,
    rows,
    newLine: Math.max(1, first.newStart),
  };
}

function rangeLabel(start: number, count: number): string {
  return count === 1 ? String(start) : `${start},${count}`;
}
