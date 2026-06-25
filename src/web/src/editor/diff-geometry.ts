// Pure line-coordinate math for the inline diff, kept out of inline-diff.ts so it's unit-testable without
// pulling in Monaco. See diff-geometry.test.ts.

/** A line span from a diff mapping (1-based, end-exclusive), matching VSCode's LineRange shape. */
export interface LineSpan {
  startLineNumber: number;
  endLineNumberExclusive: number;
}

/**
 * Translate a review-baseline line into its live-model line using the bright diff (review baseline → model).
 * Each bright hunk shifts the lines after it by (added − removed); summing those deltas for every hunk entirely
 * above `reviewLine` gives its model position. Valid only for lines in regions UNCHANGED by that diff — which is
 * exactly where every accepted (faded) hunk sits, since a keep made the review baseline equal the model there.
 */
export function reviewToModelLine(
  brightChanges: readonly { original: LineSpan; modified: LineSpan }[],
  reviewLine: number,
): number {
  let delta = 0;
  for (const change of brightChanges) {
    if (change.original.endLineNumberExclusive > reviewLine) {
      break; // changes are ordered by line, so none past here is entirely above reviewLine
    }
    delta +=
      change.modified.endLineNumberExclusive -
      change.modified.startLineNumber -
      (change.original.endLineNumberExclusive - change.original.startLineNumber);
  }
  return reviewLine + delta;
}
