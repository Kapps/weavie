import type { Range } from "@tanstack/solid-virtual";
import { type Accessor, batch, createSignal, onCleanup } from "solid-js";
import { currentEditorOptions, onEditorOptionsChanged } from "../../editor-options";

/** The first and last mounted file. */
export type Span = readonly [number, number];

/** How many files past the overscan a rested review mounts on each side. */
export interface Reach {
  readonly before: number;
  readonly after: number;
}

// A review rests once it has held still for REST_MS, past a jump's own builds; then it mounts one more file on each
// side per step, quickly enough that a short pause refills what a flick used.
const REST_MS = 200;
const STEP_MS = 50;
// One side's reach holds at least two files, however tall, and at most eight, however short; eight is also the
// most a scroll leaves mounted past it.
const FEWEST_FILES = 2;
const MOST_FILES = 8;

const indexes = ([from, to]: Span): number[] =>
  Array.from({ length: to - from + 1 }, (_, index) => from + index);

const around = (range: Range, before: number, after: number): Span => [
  Math.max(0, range.startIndex - range.overscan - before),
  Math.min(range.count - 1, range.endIndex + range.overscan + after),
];

/**
 * The files to mount around `range`: those `kept` from before, with `rested` more on each side as far as the
 * `reach`. `fresh` when none are kept (a jump, a new file list, a tab shown again): then only the overscan mounts,
 * whatever the rest.
 */
export function mountedSpan(
  range: Range,
  rested: number,
  reach: Reach,
  kept: Span | undefined,
): { span: Span; fresh: boolean } {
  const overscan = around(range, 0, 0);
  // Files apart from the overscan would mount every file between.
  if (kept === undefined || kept[0] > overscan[1] + 1 || kept[1] < overscan[0] - 1)
    return { span: overscan, fresh: true };
  const [from, to] = around(range, Math.min(rested, reach.before), Math.min(rested, reach.after));
  return { span: [Math.min(from, kept[0]), Math.max(to, kept[1])], fresh: false };
}

/** The review's files as the mount window needs them. */
export interface MountedPage {
  /** The key of the file at `index`. */
  keyAt(index: number): unknown;
  /** Whether any file is on the page; a hidden review has none. */
  rendered(): boolean;
  /** The height of the file at `index`, measured or estimated. */
  sizeAt(index: number): number;
  /** The height of the review's viewport. */
  screen(): number;
}

/**
 * Mounts the files ahead while a review rests: `editor.reviewScreensAhead` screens of them past its overscan, so
 * scrolling into a neighbouring file builds no editor. A scroll unmounts nothing; the next rest lets go of what
 * it left behind.
 */
export function createMountWindow(page: MountedPage): {
  /** The virtualizer's range extractor. */
  extractor: Accessor<(range: Range) => number[]>;
  /** Reports the scroll position, in one batch with the virtualizer hearing of it. */
  scrolled(position: number): void;
  /** Reports a scroll by `delta` that only keeps the page in place as a file above it was sized: not a move. */
  corrected(delta: number): void;
  /** Reports that the virtualizer's sizes or range changed, so the screens ahead may hold other files. */
  changed(): void;
} {
  const [screens, setScreens] = createSignal(currentEditorOptions().reviewScreensAhead);
  const [rested, setRested] = createSignal(0);
  // Files past the reach that stay mounted: all a scroll left, then one fewer per step of rest.
  const [spare, setSpare] = createSignal(0);
  // The range the extractor last saw, and the files it left mounted past the reach.
  let shown: Range | undefined;
  let excess = 0;
  let timer: ReturnType<typeof setTimeout> | undefined;
  let disposed = false;
  // The files past the overscan on each side that fill the screens ahead, by their heights as they stand.
  const reachOf = (range: Range): Reach => {
    if (screens() === 0) return { before: 0, after: 0 };
    const [low, high] = around(range, 0, 0);
    const wanted = screens() * page.screen();
    const filling = (first: number, direction: 1 | -1): number => {
      let files = 0;
      for (
        let index = first, covered = 0;
        index >= 0 &&
        index < range.count &&
        (covered < wanted || files < FEWEST_FILES) &&
        files < MOST_FILES;
        index += direction
      ) {
        covered += page.sizeAt(index);
        files++;
      }
      return files;
    };
    return { before: filling(low - 1, -1), after: filling(high + 1, 1) };
  };
  // Whether a step of rest would still mount a file or let one go.
  const unsettled = (): boolean => {
    const reach = shown === undefined ? { before: 0, after: 0 } : reachOf(shown);
    return rested() < Math.max(reach.before, reach.after) || spare() > 0;
  };
  // The one place a step is scheduled, so a step never runs twice over.
  const stepIn = (ms: number | undefined): void => {
    clearTimeout(timer);
    timer = ms === undefined || disposed || screens() === 0 ? undefined : setTimeout(step, ms);
  };
  const step = (): void => {
    timer = undefined;
    if (!unsettled()) return;
    batch(() => {
      setRested((level) => level + 1);
      setSpare((left) => Math.max(0, Math.min(left, excess) - 1));
    });
    stepIn(unsettled() ? STEP_MS : undefined);
  };
  const rest = (): void => {
    batch(() => {
      setRested(0);
      setSpare(screens() > 0 ? MOST_FILES : 0);
    });
    stepIn(REST_MS);
  };
  onCleanup(
    onEditorOptionsChanged((options) => {
      setScreens(options.reviewScreensAhead);
      rest();
    }),
  );
  onCleanup(() => {
    disposed = true;
    stepIn(undefined);
  });
  rest();
  let position: number | undefined;
  // The files last mounted, by key: a file keeps its editor when the list shifts it to another index.
  let keys = new Set<unknown>();
  const carried = (window: Span): Span | undefined => {
    if (!page.rendered()) return undefined;
    const held = indexes(window).filter((index) => keys.has(page.keyAt(index)));
    return held.length === 0 ? undefined : [held[0]!, held.at(-1)!];
  };
  return {
    extractor: () => {
      // A new extractor per step, so the virtualizer recomputes its files.
      rested();
      spare();
      screens();
      return (range) => {
        shown = range;
        const overscan = around(range, 0, 0);
        const reach = reachOf(range);
        // Read as the range is computed: a scroll reported in the same batch has already ended the rest.
        const kept = carried(around(range, reach.before + spare(), reach.after + spare()));
        const { span, fresh } = mountedSpan(range, rested(), reach, kept);
        // The rest was earned by files that are gone, so it starts over for these.
        if (fresh && rested() > 0) queueMicrotask(rest);
        excess = Math.max(
          0,
          overscan[0] - reach.before - span[0],
          span[1] - overscan[1] - reach.after,
        );
        const files = indexes(span);
        keys = new Set(files.map(page.keyAt));
        return files;
      };
    },
    scrolled: (top) => {
      if (top === position) return;
      position = top;
      rest();
    },
    corrected: (delta) => {
      if (position !== undefined) position += delta;
    },
    changed: () => {
      if (timer === undefined && unsettled()) stepIn(STEP_MS);
    },
  };
}
