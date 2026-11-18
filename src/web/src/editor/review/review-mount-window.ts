import { type Accessor, createEffect, createSignal, onCleanup } from "solid-js";
import { currentEditorOptions, onEditorOptionsChanged } from "../../editor-options";
import type { ReviewScroll } from "./review-scroll";

/** The files on screen, as the virtualizer hands them to its range extractor. */
export interface VisibleRange {
  startIndex: number;
  endIndex: number;
  overscan: number;
  count: number;
}

/** The first and last mounted file. */
export type Span = readonly [number, number];

// A resting review mounts one more file on each side per step, so no step holds the page for long.
const REST_MS = 200;
const STEP_MS = 150;
// Files past the reach that stay mounted through a scroll, so crossing a file unmounts nothing mid-gesture.
const RETAINED = 2;

/**
 * The files to mount around `range`: its overscan, `rested` more on each side, and whatever is `mounted` within
 * `reach` (plus {@link RETAINED} until the review has rested to its full reach).
 */
export function mountedSpan(
  range: VisibleRange,
  rested: number,
  reach: number,
  mounted: Span | undefined,
): Span {
  const around = (extra: number): Span => [
    Math.max(0, range.startIndex - range.overscan - extra),
    Math.min(range.count - 1, range.endIndex + range.overscan + extra),
  ];
  const [from, to] = around(rested);
  if (mounted === undefined) return [from, to];
  const [low, high] = around(reach + (rested === reach ? 0 : RETAINED));
  const kept: Span = [Math.max(mounted[0], low), Math.min(mounted[1], high)];
  // A jump leaves the old files apart from the new ones; keeping them would mount every file between.
  if (kept[0] > to + 1 || kept[1] < from - 1) return [from, to];
  return [Math.min(from, kept[0]), Math.max(to, kept[1])];
}

/**
 * Reports how long `owner` has rested: 0 on every scroll, then one more level per step up to `limit`. Any scroll
 * counts, a navigation included, so a jump mounts only its overscan as a wheel does. Returns its disposal.
 */
export function watchRest(
  owner: Pick<ReviewScroll, "onScroll">,
  limit: number,
  report: (level: number) => void,
): () => void {
  let timer: ReturnType<typeof setTimeout>;
  const rest = (): void => {
    clearTimeout(timer);
    let level = 0;
    report(level);
    const step = (): void => {
      report(++level);
      if (level < limit) timer = setTimeout(step, STEP_MS);
    };
    timer = setTimeout(step, REST_MS);
  };
  const off = owner.onScroll(rest);
  rest();
  return () => {
    off();
    clearTimeout(timer);
  };
}

/**
 * The review virtualizer's range extractor. A review that rests mounts up to `editor.reviewFilesAhead` files past
 * its overscan and keeps them through the next scroll, so scrolling into a neighbouring file builds no editor.
 * Replacing `files` starts the rest over, so a new file list mounts only its overscan at first.
 */
export function createMountWindow(
  scroll: Accessor<ReviewScroll | undefined>,
  files: Accessor<unknown>,
): Accessor<(range: VisibleRange) => number[]> {
  const [reach, setReach] = createSignal(currentEditorOptions().reviewFilesAhead);
  onCleanup(onEditorOptionsChanged((options) => setReach(options.reviewFilesAhead)));
  const [rested, setRested] = createSignal(0);
  createEffect(() => {
    const owner = scroll();
    const limit = reach();
    files();
    setRested(0);
    if (owner !== undefined && limit > 0) onCleanup(watchRest(owner, limit, setRested));
  });
  let mounted: Span | undefined;
  return () => {
    const level = rested();
    const limit = reach();
    return (range) => {
      mounted = mountedSpan(range, level, limit, mounted);
      return Array.from({ length: mounted[1] - mounted[0] + 1 }, (_, index) => mounted![0] + index);
    };
  };
}
