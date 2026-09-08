// Session-local Back/Forward over file and review locations, independent of their mounted widgets.

import type * as monaco from "monaco-editor";
import { samePath } from "./fs-path";

/** A surface destination with a logical reading anchor and its exact text selection/view state. */
export interface NavLocation {
  kind: "file" | "review";
  path: string;
  line: number;
  viewState?: monaco.editor.ICodeEditorViewState | null;
  anchor?: { line: number; offset: number };
}

/** Back/forward navigation over recorded editor locations, exposed to the Go Back / Go Forward commands. */
export interface NavHistory {
  /** Record the editor settling at `loc`; a small move within the current region updates the entry in place. */
  record(loc: NavLocation): void;
  /** Whether cursor events may enter history outside traversal and temporary previews. */
  canRecord(): boolean;
  /** Suspends recording during an explicitly owned preview. */
  suspend(): () => void;
  /** Cancels pending traversal immediately, preserving the last settled destination. */
  cancel(): void;
  /** Records an explicit jump even within the same nearby region. */
  push(loc: NavLocation): void;
  /** Step to the previous location; false when there's nothing behind (so the keybinding falls through). */
  back(): boolean;
  /** Step to the next location; false when there's nothing ahead. */
  forward(): boolean;
  /** Whether a previous location is available. */
  canBack(): boolean;
  /** Whether a next location is available. */
  canForward(): boolean;
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
export function createNavHistory(
  navigateTo: (loc: NavLocation) => Promise<void>,
  changed: () => void,
): NavHistory {
  const entries: NavLocation[] = [];
  // Index of the current location in `entries`; -1 until the first record.
  let index = -1;
  let settledIndex = -1;
  // >0 while a back()/forward() step's async model swap is in flight, so a settle-record that fires before the
  // swap lands (reporting the old location) doesn't truncate the history we're stepping through.
  let suspended = 0;
  const navigating = new Set<number>();
  let traversal = 0;

  const record = (loc: NavLocation, explicit: boolean): void => {
    if (navigating.size > 0 || suspended > 0) {
      return;
    }
    const current = entries[index];
    if (
      !explicit &&
      current !== undefined &&
      current.kind === loc.kind &&
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
    settledIndex = index;
  };

  const go = (target: number): boolean => {
    const loc = entries[target];
    if (loc === undefined) {
      return false;
    }
    const operation = ++traversal;
    index = target;
    navigating.clear();
    navigating.add(operation);
    void navigateTo(loc)
      .then(() => {
        if (operation === traversal) settledIndex = target;
      })
      .catch(() => {
        if (operation === traversal) index = settledIndex;
      })
      .finally(() => {
        navigating.delete(operation);
        changed();
      });
    return true;
  };

  return {
    canRecord: () => navigating.size === 0 && suspended === 0,
    cancel: () => {
      traversal += 1;
      navigating.clear();
      index = settledIndex;
      changed();
    },
    suspend: () => {
      suspended += 1;
      let released = false;
      return () => {
        if (!released) {
          released = true;
          suspended -= 1;
        }
      };
    },
    record: (loc) => record(loc, false),
    push: (loc) => record(loc, true),
    back: () => go(index - 1),
    forward: () => go(index + 1),
    canBack: () => index > 0,
    canForward: () => index >= 0 && index < entries.length - 1,
  };
}
