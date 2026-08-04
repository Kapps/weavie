const MIN_DISTANCE = 48;
const HORIZONTAL_DOMINANCE = 1.5;

interface Point {
  x: number;
  y: number;
}

export interface MobileBackSwipeCallbacks {
  onCancel: () => void;
  onCommit: () => void;
  onProgress: (progress: number) => void;
}

/** Tracks a leftward back gesture over pane chrome or non-interactive agent output. */
export function createMobileBackSwipe(callbacks: MobileBackSwipeCallbacks): {
  onTouchStart: (event: TouchEvent) => void;
  onTouchMove: (event: TouchEvent) => void;
  onTouchEnd: (event: TouchEvent) => void;
  onTouchCancel: () => void;
} {
  let start: Point | null = null;
  let latest: Point | null = null;
  let tracking = false;

  const onTouchStart = (event: TouchEvent): void => {
    const target = event.target;
    const touch = event.touches[0];
    start =
      event.touches.length === 1 &&
      touch !== undefined &&
      target instanceof Element &&
      acceptsBackSwipe(target)
        ? { x: touch.clientX, y: touch.clientY }
        : null;
    latest = start;
    tracking = false;
  };

  const onTouchMove = (event: TouchEvent): void => {
    const origin = start;
    const touch = event.touches[0];
    if (origin === null || touch === undefined) {
      return;
    }
    latest = { x: touch.clientX, y: touch.clientY };
    const dx = touch.clientX - origin.x;
    const dy = touch.clientY - origin.y;
    if (!tracking) {
      if (dx >= 0 || Math.abs(dx) <= Math.abs(dy)) {
        return;
      }
      tracking = true;
    }
    event.preventDefault();
    callbacks.onProgress(Math.min(1, Math.max(0, -dx / window.innerWidth)));
  };

  const onTouchEnd = (event: TouchEvent): void => {
    const origin = start;
    const touch = event.changedTouches[0];
    const end = touch === undefined ? latest : { x: touch.clientX, y: touch.clientY };
    start = null;
    latest = null;
    tracking = false;
    if (origin === null || end === null) {
      return;
    }
    const dx = end.x - origin.x;
    const dy = end.y - origin.y;
    if (dx <= -MIN_DISTANCE && Math.abs(dx) > Math.abs(dy) * HORIZONTAL_DOMINANCE) {
      callbacks.onProgress(Math.min(1, -dx / window.innerWidth));
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
      if (start !== null) {
        callbacks.onCancel();
      }
      start = null;
      latest = null;
      tracking = false;
    },
  };
}

function acceptsBackSwipe(target: Element): boolean {
  const surface = target.closest(".agent-surface, .terminal-surface, .editor-surface");
  if (
    surface === null ||
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
    target.closest(".pane-head") !== null ||
    (target.closest(".agent-surface") !== null && target.closest("[data-agent-composer]") === null)
  );
}
