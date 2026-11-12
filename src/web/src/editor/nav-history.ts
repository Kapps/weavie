// Session-local Back/Forward over file, review and fullscreen-pane locations, independent of their widgets.

import type * as monaco from "monaco-editor";
import { samePath } from "./fs-path";
import type { EditorSessionEntry } from "./session-types";
import { tabResourceKey } from "./tab-entry";

/** A surface destination with a logical reading anchor and its exact text selection/view state. */
export interface TextLocation {
  path: string;
  line: number;
  viewState?: monaco.editor.ICodeEditorViewState | null;
  anchor?: { line: number; offset: number };
}

export interface TabViewState {
  state: unknown | null;
  text: TextLocation | null;
}

export interface NavLocation {
  tab: Pick<EditorSessionEntry, "path" | "kind">;
  view: TabViewState;
}

/** An editor location, or the pane fullscreen shows in its place. */
export type NavDestination = NavLocation | { pane: string };

/** Back/forward navigation over recorded destinations, exposed to the Go Back / Go Forward commands. */
export interface NavHistory {
  /** Record the view settling at `loc`; a small move within the current region updates the entry in place. */
  record(loc: NavDestination): void;
  /** Whether cursor events may enter history outside traversal and temporary previews. */
  canRecord(): boolean;
  /** Suspends recording during an explicitly owned preview. */
  suspend(): () => void;
  /** Cancels pending traversal immediately, preserving the last settled destination. */
  cancel(): void;
  /** Records an explicit jump even within the same nearby region. */
  push(loc: NavDestination): void;
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

const sameRegion = (a: NavDestination, b: NavDestination): boolean => {
  if ("pane" in a || "pane" in b) return "pane" in a && "pane" in b && a.pane === b.pane;
  return (
    tabResourceKey({ ...a.tab, viewState: null }) ===
      tabResourceKey({ ...b.tab, viewState: null }) &&
    ((a.view.text === null && b.view.text === null) ||
      (a.view.text !== null &&
        b.view.text !== null &&
        samePath(a.view.text.path, b.view.text.path) &&
        Math.abs(a.view.text.line - b.view.text.line) < JUMP_LINES))
  );
};

/**
 * Creates the navigation history. `navigateTo` drives the view to a recorded location and resolves once the
 * (async) model swap has landed. Records are suppressed for the whole span of that swap: mid-swap the editor
 * still reports the *previous* file, so an un-guarded settle-record would look like a fresh jump and truncate
 * the forward history — so this promise being awaited is load-bearing, not decorative. Back and Forward step
 * over entries that are not `reachable`: a fullscreen pane's entry outlives fullscreen.
 */
export function createNavHistory(
  navigateTo: (loc: NavDestination) => Promise<void>,
  changed: () => void,
  reachable: (loc: NavDestination) => boolean,
): NavHistory {
  const entries: NavDestination[] = [];
  // Index of the current location in `entries`; -1 until the first record.
  let index = -1;
  let settledIndex = -1;
  // >0 while a back()/forward() step's async model swap is in flight, so a settle-record that fires before the
  // swap lands (reporting the old location) doesn't truncate the history we're stepping through.
  let suspended = 0;
  const navigating = new Set<number>();
  let traversal = 0;

  const record = (loc: NavDestination, explicit: boolean): void => {
    if (navigating.size > 0 || suspended > 0) {
      return;
    }
    const current = entries[index];
    if (!explicit && current !== undefined && sameRegion(current, loc)) {
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

  const step = (direction: -1 | 1): number => {
    let target = index + direction;
    for (let loc = entries[target]; loc !== undefined && !reachable(loc); loc = entries[target]) {
      target += direction;
    }
    return target;
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
    back: () => go(step(-1)),
    forward: () => go(step(1)),
    canBack: () => entries[step(-1)] !== undefined,
    canForward: () => entries[step(1)] !== undefined,
  };
}
