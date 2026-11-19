import type { Range } from "@tanstack/solid-virtual";
import { type Accessor, createSignal, onCleanup } from "solid-js";
import { currentEditorOptions, onEditorOptionsChanged } from "../../editor-options";

/** The first and last mounted file. */
export type Span = readonly [number, number];

/** How many files past the overscan are mounted on each side. */
interface Reach {
  readonly before: number;
  readonly after: number;
}

// A review rests once it has held still for REST_MS, past a jump's own builds; then each side moves one file
// toward its reach per step, quickly enough that a short pause refills what a flick used.
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

const toward = (from: number, to: number): number => from + Math.sign(to - from);

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
 * Mounts the files ahead while a review rests: `editor.reviewScreensAhead` screens of them on each side of its
 * overscan, so scrolling into a neighbouring file builds no editor. A scroll unmounts nothing; the next rest lets
 * go of what it left behind, one file per step.
 */
export function createMountWindow(page: MountedPage): {
  /** The virtualizer's range extractor. */
  extractor: Accessor<(range: Range) => number[]>;
  /** Reports the scroll position, in one batch with the virtualizer hearing of it. */
  scrolled(position: number): void;
  /** Runs `shift`, which only keeps the page in place as something above it was sized: not a move. */
  correcting(shift: () => void): void;
  /** Reports that sizes, the viewport or the range changed, so each side's reach may have moved. */
  changed(): void;
} {
  const [screens, setScreens] = createSignal(currentEditorOptions().reviewScreensAhead);
  // Bumped per step, so the virtualizer recomputes its files.
  const [steps, setSteps] = createSignal(0);
  // While resting, the files held past the overscan on each side; undefined while scrolling keeps all mounted.
  // Read as each range is computed: a scroll reported in the same batch has already ended the rest.
  let held: Reach | undefined;
  // What the extractor last returned, for the rest to step from.
  let shown: { range: Range; span: Span } | undefined;
  let keys = new Set<unknown>();
  let position: number | undefined;
  let correcting = false;
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
  // Each side one file nearer its reach, or undefined once both are there.
  const next = (): Reach | undefined => {
    if (shown === undefined) return undefined;
    const [low, high] = around(shown.range, 0, 0);
    const before = low - shown.span[0];
    const after = shown.span[1] - high;
    const reach = reachOf(shown.range);
    const step = { before: toward(before, reach.before), after: toward(after, reach.after) };
    return step.before === before && step.after === after ? undefined : step;
  };
  // The one place a step is scheduled, so a step never runs twice over.
  const stepIn = (ms: number | undefined): void => {
    clearTimeout(timer);
    timer = ms === undefined || disposed ? undefined : setTimeout(step, ms);
  };
  const step = (): void => {
    timer = undefined;
    // A hidden or emptied review extracts nothing to step from; showing it extracts afresh.
    if (!page.rendered()) return;
    const reach = next();
    if (reach === undefined) return;
    held = reach;
    setSteps((count) => count + 1);
    stepIn(STEP_MS);
  };
  const changed = (): void => {
    if (timer === undefined && page.rendered() && next() !== undefined) stepIn(STEP_MS);
  };
  const rest = (): void => {
    held = undefined;
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
  // The files last mounted within `window`, by key: a file keeps its editor when the list shifts its index.
  const carried = (window: Span): Span | undefined => {
    if (!page.rendered()) return undefined;
    const kept = indexes(window).filter((index) => keys.has(page.keyAt(index)));
    return kept.length === 0 ? undefined : [kept[0]!, kept.at(-1)!];
  };
  return {
    extractor: () => {
      steps();
      screens();
      return (range) => {
        const overscan = around(range, 0, 0);
        const reach = reachOf(range);
        const kept = carried(around(range, reach.before + MOST_FILES, reach.after + MOST_FILES));
        // Nothing kept beside the overscan (a first render, a jump, a new file list, a tab shown again): it mounts
        // alone, and the whole rest starts over from it.
        const fresh = kept === undefined || kept[0] > overscan[1] + 1 || kept[1] < overscan[0] - 1;
        if (fresh) rest();
        const span: Span = fresh
          ? overscan
          : held === undefined
            ? [Math.min(kept[0], overscan[0]), Math.max(kept[1], overscan[1])]
            : around(range, held.before, held.after);
        shown = { range, span };
        const files = indexes(span);
        keys = new Set(files.map(page.keyAt));
        if (!fresh) changed();
        return files;
      };
    },
    scrolled: (top) => {
      if (top === position) return;
      position = top;
      if (!correcting) rest();
    },
    correcting: (shift) => {
      correcting = true;
      try {
        shift();
      } finally {
        correcting = false;
      }
    },
    changed,
  };
}
