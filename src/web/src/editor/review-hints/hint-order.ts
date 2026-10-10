/** A point in the cross-file hint order; a cursor uses ±Infinity as its id so it never equals a hint. */
export interface HintPosition {
  path: string;
  line: number;
  id: number;
}

const compare = (a: HintPosition, b: HintPosition): number =>
  a.path < b.path ? -1 : a.path > b.path ? 1 : a.line - b.line || a.id - b.id;

/**
 * The next (1) or previous (-1) hint after `from` across files, wrapping around; from no position, the first (or
 * last). Undefined when there are none.
 */
export function stepHint<T extends HintPosition>(
  hints: readonly T[],
  from: HintPosition | undefined,
  direction: 1 | -1,
): T | undefined {
  const ordered = [...hints].sort((a, b) => compare(a, b) * direction);
  return (from && ordered.find((hint) => compare(hint, from) * direction > 0)) ?? ordered[0];
}
