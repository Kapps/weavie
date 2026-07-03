// A browser-style back/forward navigation history over editor locations (file + line). Records a location
// whenever the editor settles at a "jump" — a different file, or a far-enough cursor move within the same
// file — and steps through it on back()/forward(). VS Code's "Go Back" / "Go Forward", driven by the
// navigation commands and the back/forward mouse buttons.

import { samePath } from "./fs-path";

/** A point in navigation history: a file and the 1-based line the cursor rested on. */
export interface NavLocation {
  path: string;
  line: number;
}

/**
 * The line to record when leaving a file, given the 1-based cursor line and the visible line span `[top, bottom]`.
 * Undefined when the cursor is still on screen (its own settle-record is the right point); the viewport centre when
 * the user has scrolled the cursor out of view, so Back returns to where they were looking, not the off-screen cursor.
 */
export function leaveLine(cursorLine: number, top: number, bottom: number): number | undefined {
  return cursorLine >= top && cursorLine <= bottom ? undefined : Math.round((top + bottom) / 2);
}

/** Back/forward navigation over recorded editor locations, exposed to the Go Back / Go Forward commands. */
export interface NavHistory {
  /** Record the editor settling at `loc`; a small move within the current region updates the entry in place. */
  record(loc: NavLocation): void;
  /** Step to the previous location; false when there's nothing behind (so the keybinding falls through). */
  back(): boolean;
  /** Step to the next location; false when there's nothing ahead. */
  forward(): boolean;
}

// A same-file move shorter than this many lines updates the current entry in place rather than pushing a new
// one, so Back doesn't stop at every small cursor nudge (matches VS Code's jump granularity).
const JUMP_LINES = 10;
// Cap the history so a long session can't grow it without bound; the oldest entries fall off the front.
const LIMIT = 50;

/**
 * Creates the navigation history. `navigateTo` drives the editor to a recorded location and resolves once the
 * (async) model swap has landed. Records are suppressed for the whole span of that swap: mid-swap the editor
 * still reports the *previous* file, so an un-guarded settle-record would look like a fresh jump and truncate
 * the forward history — so this promise being awaited is load-bearing, not decorative.
 */
export function createNavHistory(navigateTo: (loc: NavLocation) => Promise<void>): NavHistory {
  const entries: NavLocation[] = [];
  // Index of the current location in `entries`; -1 until the first record.
  let index = -1;
  // >0 while a back()/forward() step's async model swap is in flight, so a settle-record that fires before the
  // swap lands (reporting the old location) doesn't truncate the history we're stepping through.
  let navigating = 0;

  const record = (loc: NavLocation): void => {
    if (navigating > 0) {
      return;
    }
    const current = entries[index];
    if (
      current !== undefined &&
      samePath(current.path, loc.path) &&
      Math.abs(current.line - loc.line) < JUMP_LINES
    ) {
      // Same region: keep the entry anchored to the latest position so Back returns precisely here.
      entries[index] = loc;
      return;
    }
    // A fresh jump drops any forward history (browser semantics), then appends and points at it.
    entries.length = index + 1;
    entries.push(loc);
    if (entries.length > LIMIT) {
      entries.shift();
    }
    index = entries.length - 1;
  };

  const go = (target: number): boolean => {
    const loc = entries[target];
    if (loc === undefined) {
      return false;
    }
    index = target;
    navigating += 1;
    void navigateTo(loc).finally(() => {
      navigating -= 1;
    });
    return true;
  };

  return {
    record,
    back: () => go(index - 1),
    forward: () => go(index + 1),
  };
}
