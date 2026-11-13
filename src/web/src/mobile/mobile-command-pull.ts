import { createSignal, onCleanup } from "solid-js";
import { startsOnBrowserEdge } from "./browser-edge";
import { SWIPE_INTENT_DISTANCE } from "./mobile-back-swipe";
import { roomAbove, TOUCH_CONTROLS } from "./touch-drag-owner";

const OPEN_DISTANCE = 64;

/** Pulling down where nothing under the finger takes the drag opens commands on release. */
export function createMobileCommandPull(callbacks: {
  canStart: () => boolean;
  onOpen: () => void;
}) {
  const [hint, setHint] = createSignal("");
  let scope: Element | null = null;
  let start: { x: number; y: number } | null = null;
  let claimed = false;
  let distance = 0;

  const reset = (): void => {
    start = null;
    claimed = false;
    distance = 0;
    setHint("");
  };

  const handlers = {
    touchstart(event: TouchEvent): void {
      reset();
      const touch = event.touches[0];
      if (
        !callbacks.canStart() ||
        event.touches.length !== 1 ||
        touch === undefined ||
        startsOnBrowserEdge(touch.clientX) ||
        !(event.target instanceof Element) ||
        window.getSelection()?.isCollapsed === false ||
        !acceptsPull(event.target, scope)
      ) {
        return;
      }
      start = { x: touch.clientX, y: touch.clientY };
    },
    touchmove(event: TouchEvent): void {
      if (start === null) return;
      const touch = event.touches[0];
      if (
        event.defaultPrevented ||
        event.touches.length !== 1 ||
        touch === undefined ||
        !callbacks.canStart()
      ) {
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
    touchend(event: TouchEvent): void {
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
    touchcancel: reset,
  };
  // On window, so a surface that took the drag has already said so by the time the pull sees it.
  const listening = new AbortController();
  for (const [type, handler] of Object.entries(handlers)) {
    window.addEventListener(type, handler as EventListener, {
      passive: false,
      signal: listening.signal,
    });
  }
  onCleanup(() => listening.abort());
  return {
    hint,
    scope(element: Element): void {
      scope = element;
    },
  };
}

function acceptsPull(target: Element, scope: Element | null): boolean {
  // Monaco and xterm own their scrolling, so a pane head offers the pull across its tabs and buttons too.
  const controls = target.closest(".pane-head, .pane-tabs") === null ? `, ${TOUCH_CONTROLS}` : "";
  if (
    scope?.contains(target) !== true ||
    target.closest(
      `.monaco-editor, .xterm, .mobile-surface-bar, [data-agent-composer]${controls}`,
    ) !== null
  )
    return false;
  for (let element: Element = target; element !== scope; element = element.parentElement!) {
    const style = getComputedStyle(element);
    // A surface that pans itself, or a scroller with content above, takes a downward drag.
    if (!/^(auto|manipulation)$|pan-(y|up|down)/.test(style.touchAction)) return false;
    const bottomOrigin = style.display.endsWith("flex") && style.flexDirection === "column-reverse";
    if (/^(auto|scroll)$/.test(style.overflowY) && roomAbove(element, bottomOrigin) > 1)
      return false;
  }
  return true;
}
