import { normalizePath } from "../fs-path";
import type { PrThread } from "../pr-comments/pr-comments-store";
import type { ReviewHint } from "../review-hints/review-hints-store";

/** A PR thread or review hint, keyed so the two kinds share one sequence. */
export type ItemKey = `hint:${number}` | `thread:${string}`;

/** A place in the diff: a file and a line in it. */
export interface ItemLocation {
  path: string;
  line: number;
}

/** One stop in the sequence: an item at the line it was made on. */
export interface ReviewItem extends ItemLocation {
  key: ItemKey;
}

export interface ItemFilter {
  comments: boolean;
  hints: boolean;
  /** Include resolved and outdated threads. */
  settled: boolean;
}

/** Diff order: the review's files first in its own order, then other files by path; within a file by line. */
export function diffOrder(
  reviewPaths: readonly string[],
): (a: ItemLocation, b: ItemLocation) => number {
  const rank = new Map(reviewPaths.map((path, index) => [normalizePath(path), index]));
  const key = (path: string) => {
    const file = normalizePath(path);
    return { rank: rank.get(file) ?? reviewPaths.length, file };
  };
  return (a, b) => {
    const left = key(a.path);
    const right = key(b.path);
    return (
      left.rank - right.rank ||
      (left.file < right.file ? -1 : left.file > right.file ? 1 : 0) ||
      a.line - b.line
    );
  };
}

/** Every shown thread and hint in diff order, threads before hints on the same line. */
export function buildSequence(
  threads: readonly PrThread[],
  hints: readonly ReviewHint[],
  reviewPaths: readonly string[],
  filter: ItemFilter,
): ReviewItem[] {
  const order = diffOrder(reviewPaths);
  const items: ReviewItem[] = [
    ...(filter.comments ? threads : [])
      .filter((thread) => filter.settled || !(thread.resolved || thread.outdated))
      .map((thread) => ({
        key: `thread:${thread.id}` as const,
        path: thread.path,
        line: thread.line,
      })),
    ...(filter.hints ? hints : []).map((hint) => ({
      key: `hint:${hint.id}` as const,
      path: hint.path,
      line: hint.line,
    })),
  ];
  // Stable: threads were listed first, so they stay ahead of hints on the same line.
  return items.sort(order);
}

/**
 * The item after (1) or before (-1) `origin`, wrapping: an item in the sequence steps to its neighbour, a location
 * to the nearest item past it, and no origin to the first (or last). Undefined when the sequence is empty.
 */
export function stepItem(
  sequence: readonly ReviewItem[],
  reviewPaths: readonly string[],
  origin: ItemKey | ItemLocation | null,
  direction: 1 | -1,
): ReviewItem | undefined {
  const edge = direction === 1 ? sequence[0] : sequence.at(-1);
  if (origin === null) return edge;
  if (typeof origin === "string") {
    const index = sequence.findIndex((item) => item.key === origin);
    return index < 0 ? edge : sequence[(index + direction + sequence.length) % sequence.length];
  }
  const order = diffOrder(reviewPaths);
  const found =
    direction === 1
      ? sequence.find((item) => order(item, origin) > 0)
      : sequence.findLast((item) => order(item, origin) < 0);
  return found ?? edge;
}
