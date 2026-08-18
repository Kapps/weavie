import { BROWSER_EDGE_WIDTH, startsOnBrowserEdge } from "./browser-edge";

const MIN_DISTANCE = 48;
const INTENT_DISTANCE = 12;
const HORIZONTAL_DOMINANCE = 1.5;
// xterm owns the terminal body, so a terminal back swipe gets the strip just inside the browser's edge.
const TERMINAL_EDGE_LIMIT = BROWSER_EDGE_WIDTH + 32;

interface Point {
  x: number;
  y: number;
}

type GestureState = "idle" | "pending" | "horizontal" | "rejected";

export interface MobileBackSwipeCallbacks {
  canStart: () => boolean;
  onCancel: () => void;
  onCommit: () => void;
  onProgress: (progress: number) => void;
  onStart: () => void;
}

/** Tracks a rightward back gesture over pane chrome or non-interactive agent output. */
export function createMobileBackSwipe(callbacks: MobileBackSwipeCallbacks): {
  onTouchStart: (event: TouchEvent) => void;
  onTouchMove: (event: TouchEvent) => void;
  onTouchEnd: (event: TouchEvent) => void;
  onTouchCancel: () => void;
} {
  let start: Point | null = null;
  let latest: Point | null = null;
  let state: GestureState = "idle";

  const reset = (): void => {
    start = null;
    latest = null;
    state = "idle";
  };

  const onTouchStart = (event: TouchEvent): void => {
    if (state === "horizontal") {
      callbacks.onCancel();
    }
    const target = event.target;
    const touch = event.touches[0];
    start =
      callbacks.canStart() &&
      event.touches.length === 1 &&
      touch !== undefined &&
      target instanceof Element &&
      acceptsBackSwipe(target, touch.clientX)
        ? { x: touch.clientX, y: touch.clientY }
        : null;
    latest = start;
    state = start === null ? "idle" : "pending";
  };

  const onTouchMove = (event: TouchEvent): void => {
    const origin = start;
    const touch = event.touches[0];
    if (origin === null || touch === undefined || state === "rejected") {
      return;
    }
    if (event.touches.length !== 1) {
      if (state === "horizontal") {
        callbacks.onCancel();
      }
      state = "rejected";
      return;
    }
    latest = { x: touch.clientX, y: touch.clientY };
    const dx = touch.clientX - origin.x;
    const dy = touch.clientY - origin.y;
    if (state === "pending") {
      if (Math.max(Math.abs(dx), Math.abs(dy)) < INTENT_DISTANCE) {
        return;
      }
      if (dx <= 0 || Math.abs(dx) <= Math.abs(dy) * HORIZONTAL_DOMINANCE) {
        state = "rejected";
        return;
      }
      state = "horizontal";
      callbacks.onStart();
    }
    event.preventDefault();
    callbacks.onProgress(Math.min(1, Math.max(0, dx / window.innerWidth)));
  };

  const onTouchEnd = (event: TouchEvent): void => {
    const origin = start;
    const touch = event.changedTouches[0];
    const end = touch === undefined ? latest : { x: touch.clientX, y: touch.clientY };
    const completedState = state;
    reset();
    if (completedState !== "horizontal" || origin === null || end === null) {
      return;
    }
    const dx = end.x - origin.x;
    const dy = end.y - origin.y;
    if (dx >= MIN_DISTANCE && Math.abs(dx) > Math.abs(dy) * HORIZONTAL_DOMINANCE) {
      callbacks.onProgress(Math.min(1, dx / window.innerWidth));
      callbacks.onCommit();
    } else {
      callbacks.onCancel();
    }
  };

  return {
    onTouchStart,
    onTouchMove,
    onTouchEnd,
    onTouchCancel: () => {
      if (state === "horizontal") {
        callbacks.onCancel();
      }
      reset();
    },
  };
}

function acceptsBackSwipe(target: Element, startX: number): boolean {
  const surface = target.closest(".agent-surface, .terminal-surface, .editor-surface");
  if (
    surface === null ||
    startsOnBrowserEdge(startX) ||
    target.closest(
      "button, a, input, textarea, select, summary, [contenteditable], [role='button'], [role='link'], [role='menuitem'], [role='option'], [tabindex]:not([tabindex='-1'])",
    ) !== null
  ) {
    return false;
  }
  for (
    let element: Element | null = target;
    element !== null && element !== surface;
    element = element.parentElement
  ) {
    if (!(element instanceof HTMLElement)) {
      continue;
    }
    const overflow = getComputedStyle(element).overflowX;
    if (
      (overflow === "auto" || overflow === "scroll") &&
      element.scrollWidth > element.clientWidth
    ) {
      return false;
    }
  }
  return (
    target.closest(".pane-head, .editor-tabs") !== null ||
    (startX <= TERMINAL_EDGE_LIMIT && target.closest(".terminal-surface") !== null) ||
    (target.closest(".agent-surface") !== null && target.closest("[data-agent-composer]") === null)
  );
}
