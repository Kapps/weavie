// The unified review's own walk: stepping through the changes on the page instead of opening the file behind
// it. Each mounted file section publishes its live diff geometry here, so a step can land on an exact change.

import { normalizePath, samePath } from "../fs-path";
import type { ReviewFileView, UnifiedReviewNavigator } from "./review-store";

/** One mounted section's live diff. `element` is the editor mount; every accessor answers for right now. */
export interface ReviewSection {
  element(): HTMLElement | undefined;
  painted(): boolean;
  changeLines(): number[];
  topForLine(line: number): number;
}

/** How a section publishes itself. `clear` is identity-guarded, so a late teardown can't drop a newer mount. */
export interface ReviewSectionRegistry {
  set(path: string, section: ReviewSection): void;
  clear(path: string, section: ReviewSection): void;
}

/** The surface the walk moves: its file list, its selection, and its scrolling. */
export interface ReviewWalkSurface {
  files(): ReviewFileView[];
  currentIndex(): number;
  /** Make `index` the current file and publish the walk's position; the surface owns follow-mode bookkeeping. */
  select(index: number, path: string, line: number): void;
  expand(file: ReviewFileView): void;
  scroller(): HTMLElement | undefined;
  scrollToIndex(index: number): void;
}

export interface ReviewWalk extends UnifiedReviewNavigator {
  sections: ReviewSectionRegistry;
  /** Land on a file the user picked out of the tree, opening it whether or not it still needs review. */
  goToFile(file: ReviewFileView): void;
  /** Re-anchor the walk after the viewport or focus moved the current file under it. */
  anchor(line: number): void;
}

/** Where a step wants to land inside a section: an exact line, or whichever end it stepped in from. */
type Spot = number | "first" | "last";

const wrap = (index: number, length: number): number => ((index % length) + length) % length;
const edgeFor = (delta: 1 | -1): Spot => (delta === 1 ? "first" : "last");
const needsReview = (file: ReviewFileView): boolean => !file.loaded() || file.pending();

export function createReviewWalk(surface: ReviewWalkSurface, headerHeight: number): ReviewWalk {
  const sections = new Map<string, ReviewSection>();
  // A step that reached a section before its geometry existed; anchored when that section paints.
  let queued: { key: string; spot: Spot } | null = null;
  // The line the walk stands on. Its own state — the published cursor lags a step behind.
  let line = 0;

  const keyOf = (file: ReviewFileView): string => normalizePath(file.summary().path);
  const lineFor = (section: ReviewSection, spot: Spot): number | undefined => {
    if (typeof spot === "number") {
      return spot;
    }
    const lines = section.changeLines();
    return spot === "first" ? lines[0] : lines.at(-1);
  };

  // Scroll the page so `target` clears the section's sticky header. Read from live rects, so it is correct
  // wherever the virtualizer has placed the section.
  const revealLine = (section: ReviewSection, target: number): void => {
    const scroller = surface.scroller();
    const element = section.element();
    if (scroller === undefined || element === undefined) {
      return;
    }
    const top =
      element.getBoundingClientRect().top -
      scroller.getBoundingClientRect().top +
      scroller.scrollTop +
      section.topForLine(target);
    scroller.scrollTo({ top: Math.max(0, top - headerHeight) });
  };

  const indexOfKey = (key: string): number =>
    surface.files().findIndex((file) => keyOf(file) === key);

  const settle = (key: string): void => {
    const pending = queued;
    const section = sections.get(key);
    if (pending === null || pending.key !== key || section === undefined || !section.painted()) {
      return;
    }
    queued = null;
    const target = lineFor(section, pending.spot);
    const file = surface.files()[indexOfKey(key)];
    if (target === undefined || file === undefined) {
      return;
    }
    line = target;
    surface.select(indexOfKey(key), file.summary().path, target);
    // "first" already landed: a section collapses its unchanged regions, so its top is its first change.
    if (pending.spot !== "first") {
      revealLine(section, target);
    }
  };

  // Land on a spot. Inside the section already on screen the walk scrolls straight to the change. Reaching a
  // different one hands the coarse move to the virtualizer — which owns the offsets of a list it is still
  // measuring — and only then, once the section has settled at its final position, reveals the exact line.
  const goTo = (index: number, spot: Spot, open: boolean): boolean => {
    const file = surface.files()[index];
    if (file === undefined) {
      return false;
    }
    const summary = file.summary();
    const key = keyOf(file);
    const section = sections.get(key);
    const target =
      index === surface.currentIndex() && section !== undefined && section.painted()
        ? lineFor(section, spot)
        : undefined;
    if (open) {
      surface.expand(file);
    }
    if (section !== undefined && target !== undefined) {
      queued = null;
      line = target;
      surface.select(index, summary.path, target);
      queueMicrotask(() => revealLine(section, target));
      return true;
    }
    queued = { key, spot };
    line = summary.line;
    surface.select(index, summary.path, summary.line);
    queueMicrotask(() => {
      surface.scrollToIndex(index + 1);
      requestAnimationFrame(() => settle(key));
    });
    return true;
  };

  // Walk the changes: the next spot inside the file on screen, else the nearest file that still needs review.
  // Wraps, so a single-file review cycles its own hunks.
  const stepChange = (delta: 1 | -1): boolean => {
    const files = surface.files();
    if (files.length === 0) {
      return false;
    }
    const from = surface.currentIndex();
    const section = sections.get(keyOf(files[from] ?? files[0]!));
    if (section !== undefined) {
      const lines = section.changeLines();
      const next =
        delta === 1
          ? lines.find((candidate) => candidate > line)
          : lines.filter((candidate) => candidate < line).at(-1);
      if (next !== undefined) {
        return goTo(from, next, true);
      }
    }
    for (let step = 1; step <= files.length; step++) {
      const index = wrap(from + delta * step, files.length);
      const candidate = files[index];
      if (candidate !== undefined && needsReview(candidate)) {
        return goTo(index, edgeFor(delta), true);
      }
    }
    return false;
  };

  // Walk the file axis: every file, reviewed ones included — but stepping past a file the user already folded
  // away must not silently re-open it, so only a file that still needs review is expanded.
  const stepFile = (delta: 1 | -1): boolean => {
    const files = surface.files();
    if (files.length < 2) {
      return false;
    }
    const index = wrap(surface.currentIndex() + delta, files.length);
    return goTo(index, edgeFor(delta), needsReview(files[index]!));
  };

  return {
    sections: {
      set: (path, section) => {
        const key = normalizePath(path);
        sections.set(key, section);
        settle(key);
      },
      clear: (path, section) => {
        const key = normalizePath(path);
        if (sections.get(key) === section) {
          sections.delete(key);
        }
      },
    },
    goToFile: (file) => {
      const index = surface
        .files()
        .findIndex((candidate) => samePath(candidate.summary().path, file.summary().path));
      if (index >= 0) {
        goTo(index, "first", true);
      }
    },
    anchor: (at) => {
      line = at;
      queued = null;
    },
    nextChange: () => stepChange(1),
    prevChange: () => stepChange(-1),
    nextFile: () => stepFile(1),
    prevFile: () => stepFile(-1),
  };
}
