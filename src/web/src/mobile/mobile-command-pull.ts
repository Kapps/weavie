import { createSignal } from "solid-js";
import { startsOnBrowserEdge } from "./browser-edge";
import { SWIPE_INTENT_DISTANCE } from "./mobile-back-swipe";

const OPEN_DISTANCE = 64;

/** Pulling from a native scroller's top or pane chrome opens commands on release. */
export function createMobileCommandPull(callbacks: {
  canStart: () => boolean;
  onOpen: () => void;
}) {
  const [hint, setHint] = createSignal("");
  let start: { x: number; y: number } | null = null;
  let claimed = false;
  let distance = 0;

  const reset = (): void => {
    start = null;
    claimed = false;
    distance = 0;
    setHint("");
  };

  return {
    hint,
    onTouchStart(event: TouchEvent): void {
      reset();
      const touch = event.touches[0];
      if (
        !callbacks.canStart() ||
        event.touches.length !== 1 ||
        touch === undefined ||
        startsOnBrowserEdge(touch.clientX) ||
        touch.clientY < 32 ||
        !(event.target instanceof Element) ||
        window.getSelection()?.isCollapsed === false ||
        !acceptsPull(event.target, event.currentTarget as Element)
      ) {
        return;
      }
      start = { x: touch.clientX, y: touch.clientY };
    },
    onTouchMove(event: TouchEvent): void {
      if (start === null) return;
      const touch = event.touches[0];
      if (event.touches.length !== 1 || touch === undefined || !callbacks.canStart()) {
        reset();
        return;
      }
      const dx = touch.clientX - start.x;
      const dy = touch.clientY - start.y;
      if (!claimed) {
        if (Math.max(Math.abs(dx), Math.abs(dy)) < SWIPE_INTENT_DISTANCE) return;
        if (dy <= Math.abs(dx) * 1.5) {
          reset();
          return;
        }
        claimed = true;
      }
      if (!event.cancelable) {
        reset();
        return;
      }
      event.preventDefault();
      distance = Math.max(0, dy);
      setHint(distance >= OPEN_DISTANCE ? "Release for commands" : "Pull for commands");
    },
    onTouchEnd(event: TouchEvent): void {
      const touch = event.changedTouches[0];
      const open =
        claimed &&
        start !== null &&
        touch !== undefined &&
        touch.clientY - start.y >= OPEN_DISTANCE &&
        distance >= OPEN_DISTANCE &&
        event.touches.length === 0 &&
        callbacks.canStart();
      if (claimed && event.cancelable) event.preventDefault();
      reset();
      if (open) callbacks.onOpen();
    },
    onTouchCancel: reset,
  };
}

function acceptsPull(target: Element, boundary: Element): boolean {
  // Monaco and xterm own virtual scrolling and selection; their headers still offer the gesture.
  if (
    target.closest(
      "button, a, input, textarea, select, summary, [contenteditable], [role='button'], [role='link'], [role='option'], [role='menuitem'], .monaco-editor, .xterm, .mobile-surface-bar, [data-agent-composer]",
    ) !== null
  )
    return false;
  for (let element: Element | null = target; element !== null; element = element.parentElement) {
    if (element instanceof HTMLElement && element.scrollTop > 0) return false;
    if (element === boundary) return true;
  }
  return false;
}
