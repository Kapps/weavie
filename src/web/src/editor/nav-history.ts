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
 * Creates the navigation history. `navigateTo` drives the editor to a recorded location; the (debounced)
 * settle that follows re-records that same location, which coalesces back into the current entry — so a
 * back/forward step never grows the history or drops the entries on the other side of it.
 */
export function createNavHistory(navigateTo: (loc: NavLocation) => void): NavHistory {
  const entries: NavLocation[] = [];
  // Index of the current location in `entries`; -1 until the first record.
  let index = -1;

  const record = (loc: NavLocation): void => {
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
    navigateTo(loc);
    return true;
  };

  return {
    record,
    back: () => go(index - 1),
    forward: () => go(index + 1),
  };
}
