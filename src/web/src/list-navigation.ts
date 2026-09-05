import { type Accessor, createSignal, type Setter } from "solid-js";

/** Where an arrow key past the last (or first) row lands: back around, or stopped at the end. */
export type ListEdges = "wrap" | "clamp";

/** The row an arrow key moves to. The one source of truth for every highlight-a-row list in the app. */
export function nextIndex(current: number, delta: 1 | -1, count: number, edges: ListEdges): number {
  if (edges === "clamp") {
    return delta === 1 ? Math.min(current + 1, count - 1) : Math.max(current - 1, 0);
  }
  if (count === 0) {
    return current;
  }
  return delta === 1 ? (current + 1) % count : current <= 0 ? count - 1 : current - 1;
}

export interface ListNavigationOptions {
  /** How many rows are navigable right now. */
  count: Accessor<number>;
  edges: ListEdges;
  /** The row highlighted before any key lands; -1 means "nothing highlighted yet". */
  initialIndex: number;
  /** Keys that act on the highlighted row (Enter, and Tab where the list completes an input). */
  acceptKeys: readonly string[];
  onAccept: (index: number, event: KeyboardEvent) => void;
  /** Escape. */
  onDismiss: () => void;
  /** Runs after an arrow moved the highlight — scroll it into view, live-preview it. */
  onMove?: (index: number) => void;
  /** Stop the keys this owns from propagating (a window-capture overlay that must beat other listeners). */
  stopPropagation?: boolean;
  /** Swallow the arrow keys even with an empty list. */
  consumeEmptyArrows?: boolean;
  /** Report the index inside the live count, so a list that shrank never leaves it dangling past the end. */
  clampIndex?: boolean;
}

export interface ListNavigation {
  index: Accessor<number>;
  setIndex: Setter<number>;
  /** True when the key was one of the list's own. */
  onKeyDown: (event: KeyboardEvent) => boolean;
}

/**
 * Arrow-key row navigation for a menu, typeahead, or palette: owns the highlighted index and the keyboard
 * protocol around it (arrows move, `acceptKeys` act on the row, Escape dismisses) so each list declares its
 * semantics instead of re-implementing them.
 */
export function createListNavigation(options: ListNavigationOptions): ListNavigation {
  const [rawIndex, setIndex] = createSignal(options.initialIndex);
  const index: Accessor<number> = options.clampIndex
    ? () => Math.min(rawIndex(), options.count() - 1)
    : rawIndex;

  const consume = (event: KeyboardEvent): true => {
    event.preventDefault();
    if (options.stopPropagation === true) {
      event.stopPropagation();
    }
    return true;
  };

  const onKeyDown = (event: KeyboardEvent): boolean => {
    const delta = event.key === "ArrowDown" ? 1 : event.key === "ArrowUp" ? -1 : 0;
    if (delta !== 0) {
      const count = options.count();
      if (count === 0 && options.consumeEmptyArrows !== true) {
        return false;
      }
      const moved = nextIndex(index(), delta, count, options.edges);
      setIndex(moved);
      options.onMove?.(moved);
      return consume(event);
    }
    if (options.acceptKeys.includes(event.key)) {
      const at = index();
      consume(event);
      options.onAccept(at, event);
      return true;
    }
    if (event.key === "Escape") {
      consume(event);
      options.onDismiss();
      return true;
    }
    return false;
  };

  return { index, setIndex, onKeyDown };
}
