import type { Range } from "@tanstack/solid-virtual";
import { type Accessor, createSignal, onCleanup } from "solid-js";
import { currentEditorOptions, onEditorOptionsChanged } from "../../editor-options";

/** The first and last mounted file. */
export type Span = readonly [number, number];

// A resting review mounts one more file on each side per step, so no step holds the page for long.
const REST_MS = 200;
const STEP_MS = 150;
// Files past the reach that stay mounted through a scroll, so crossing a file unmounts nothing mid-gesture.
const RETAINED = 2;

const around = (range: Range, extra: number): Span => [
  Math.max(0, range.startIndex - range.overscan - extra),
  Math.min(range.count - 1, range.endIndex + range.overscan + extra),
];

/**
 * The files to mount around `range`. Those `mounted` within `reach` stay (and {@link RETAINED} more until the
 * review has rested to its full reach), with `rested` more on each side. `fresh` when none of them carries over
 * (a jump, a new file list, a tab shown again): then only the overscan mounts, whatever the rest.
 */
export function mountedSpan(
  range: Range,
  rested: number,
  reach: number,
  mounted: Span | undefined,
): { span: Span; fresh: boolean } {
  const overscan = around(range, 0);
  if (mounted === undefined) return { span: overscan, fresh: true };
  const [low, high] = around(range, reach + (rested === reach ? 0 : RETAINED));
  const kept: Span = [Math.max(mounted[0], low), Math.min(mounted[1], high)];
  // Files apart from the overscan would mount every file between.
  if (kept[0] > overscan[1] + 1 || kept[1] < overscan[0] - 1)
    return { span: overscan, fresh: true };
  const [from, to] = around(range, rested);
  return { span: [Math.min(from, kept[0]), Math.max(to, kept[1])], fresh: false };
}

/**
 * Mounts the files ahead while a review rests: up to `editor.reviewFilesAhead` past its overscan, kept through the
 * next scroll, so scrolling into a neighbouring file builds no editor. `rendered` is the span of files on the page.
 */
export function createMountWindow(rendered: () => Span | undefined): {
  /** The virtualizer's range extractor. */
  extractor: Accessor<(range: Range) => number[]>;
  /** Reports the scroll position. Call before the virtualizer hears of it, so its range sees the rest ended. */
  scrolled(position: number): void;
} {
  const [reach, setReach] = createSignal(currentEditorOptions().reviewFilesAhead);
  const [rested, setRested] = createSignal(0);
  let timer: ReturnType<typeof setTimeout> | undefined;
  const step = (): void => {
    if (setRested((level) => level + 1) < reach()) timer = setTimeout(step, STEP_MS);
  };
  const rest = (): void => {
    clearTimeout(timer);
    setRested(0);
    if (reach() > 0) timer = setTimeout(step, REST_MS);
  };
  onCleanup(
    onEditorOptionsChanged((options) => {
      setReach(options.reviewFilesAhead);
      rest();
    }),
  );
  onCleanup(() => clearTimeout(timer));
  rest();
  let position: number | undefined;
  return {
    extractor: () => {
      const level = Math.min(rested(), reach());
      const limit = reach();
      return (range) => {
        const { span, fresh } = mountedSpan(range, level, limit, rendered());
        // The rest was earned by files that are gone, so it starts over for these.
        if (fresh && level > 0) queueMicrotask(rest);
        return Array.from({ length: span[1] - span[0] + 1 }, (_, index) => span[0] + index);
      };
    },
    scrolled: (top) => {
      if (top === position) return;
      position = top;
      rest();
    },
  };
}
