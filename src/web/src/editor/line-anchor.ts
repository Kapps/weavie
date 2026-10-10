// Maps a line numbered against one text (a PR head, a review hint's captured file) onto another, usually the live
// buffer, so inline notes follow their code through local edits.

import { computeTextDiffLines, type DiffLineChange } from "./review/diff-computation";

export type LineChanges = readonly DiffLineChange[];

export interface LineAnchor {
  afterLine: number;
  /** False when the line itself was changed; it then anchors after its replacement. */
  exact: boolean;
}

// A line inside a changed range anchors after that range's replacement: the nearest surviving position.
export function mapLine(changes: LineChanges, line: number): LineAnchor {
  let delta = 0;
  for (const change of changes) {
    if (line < change.original.startLineNumber) break;
    if (line < change.original.endLineNumberExclusive) {
      return { afterLine: change.modified.endLineNumberExclusive - 1, exact: false };
    }
    delta = change.modified.endLineNumberExclusive - change.original.endLineNumberExclusive;
  }
  return { afterLine: line + delta, exact: true };
}

export function diffLines(original: string, modified: string): LineChanges {
  const changes = computeTextDiffLines(original, modified);
  if (changes === null) throw new Error("The file is too large to place inline notes on.");
  return changes;
}
