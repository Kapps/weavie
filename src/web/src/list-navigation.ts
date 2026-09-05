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

/** Tells each live list's rows apart in the document, so one list never scrolls another's row into view. */
let listSeq = 0;

/** What a row needs to take part: the mouse follows the highlight, and it is addressable for scrolling. */
export interface ListRowProps {
  "data-list-row": string;
  onMouseMove: () => void;
}

export interface ListNavigationOptions {
  /** How many rows are navigable right now. */
  count: Accessor<number>;
  edges: ListEdges;
  /**
   * The row highlighted before any key lands. `-1` starts with nothing highlighted, which pairs with an
   * `onAccept` that acts on the typed text: the accept key reports `-1` until an arrow picks a row.
   */
  initialIndex: number;
  /** Keys that act on the highlighted row (Enter, and Tab where the list completes an input). */
  acceptKeys: readonly string[];
  /** `index` is `-1` when no row is highlighted — an empty list, or a `-1` start; the surface decides. */
  onAccept: (index: number, event: KeyboardEvent) => void;
  /** Escape. */
  onDismiss: () => void;
  /** Runs after an arrow moved the highlight — live-preview it. Scrolling it into view is automatic. */
  onMove?: (index: number) => void;
}

export interface ListNavigation {
  index: Accessor<number>;
  setIndex: Setter<number>;
  /** True when the key was one of the list's own. */
  onKeyDown: (event: KeyboardEvent) => boolean;
  /** Spread onto each row element. */
  row: (index: number) => ListRowProps;
  /** Scroll the highlighted row into view. Automatic on every arrow move; call it for other reveals. */
  reveal: (block: ScrollLogicalPosition) => void;
}

/**
 * Arrow-key row navigation for a menu, typeahead, or palette: owns the highlighted index and the keyboard
 * protocol around it (arrows move and scroll the row into view, `acceptKeys` act on the row, Escape
 * dismisses, and the list swallows every key it owns) so each list declares its semantics instead of
 * re-implementing them.
 */
export function createListNavigation(options: ListNavigationOptions): ListNavigation {
  const listId = String(++listSeq);
  const [rawIndex, setIndex] = createSignal(options.initialIndex);
  // A list that shrank under the highlight never leaves it dangling past the end; an empty list reports -1.
  const index: Accessor<number> = () => Math.min(rawIndex(), options.count() - 1);

  const reveal = (block: ScrollLogicalPosition): void => {
    // After the render the move caused, so a row that only just appeared is the one scrolled to.
    queueMicrotask(() =>
      document.querySelector(`[data-list-row="${listId}:${index()}"]`)?.scrollIntoView({ block }),
    );
  };

  const consume = (event: KeyboardEvent): true => {
    event.preventDefault();
    event.stopPropagation();
    return true;
  };

  const onKeyDown = (event: KeyboardEvent): boolean => {
    const delta = event.key === "ArrowDown" ? 1 : event.key === "ArrowUp" ? -1 : 0;
    if (delta !== 0) {
      // An empty list still swallows the key, but must not record a move: the row the list fills in with
      // is the one the next Enter should act on.
      if (options.count() > 0) {
        setIndex(nextIndex(index(), delta, options.count(), options.edges));
        options.onMove?.(index());
        reveal("nearest");
      }
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

  // mousemove, not mouseenter: a row sliding under a still cursor as the list scrolls must not steal the
  // highlight from the keyboard.
  const row = (rowIndex: number): ListRowProps => ({
    "data-list-row": `${listId}:${rowIndex}`,
    onMouseMove: () => setIndex(rowIndex),
  });

  return { index, setIndex, onKeyDown, row, reveal };
}
