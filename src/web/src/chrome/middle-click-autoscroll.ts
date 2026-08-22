// Middle-click autoscroll for every scrollable surface in the app: press the middle button, then move the
// pointer to scroll continuously — the further from the press point, the faster. Windows' engine ships this
// natively, macOS and Linux never have, so Weavie owns the gesture on all three (preventDefault on the press
// keeps Chromium from starting a second one on top of ours). Monaco drives its own virtual viewport through
// `scrollOnMiddleClick`, wired to the same setting.

import { currentEditorOptions } from "../editor-options";

// Pointer travel that scrolls at zero speed, so a press without a deliberate drag holds still.
const DEAD_ZONE = 5;
// Surfaces that own the middle button themselves: text fields and links (primary-selection paste, open),
// Monaco and xterm (their own gestures), and anything marking itself `data-middle-click`.
const OWNS_MIDDLE_CLICK =
  "a,input,textarea,select,[contenteditable]:not([contenteditable='false'])," +
  ".monaco-editor,.xterm,[data-middle-click]";
const SCROLLABLE = /^(auto|scroll|overlay)$/;

interface Surface {
  x: Element | null;
  y: Element | null;
}

// The nearest ancestor that can actually scroll, per axis — so a drag down scrolls the pane even when the
// pointer sits on something narrower that only scrolls sideways (a wide code block, a tab strip). Walks
// `Element`, not `HTMLElement`: a press on an icon targets an SVG `<path>`.
function surfaceAt(target: Element): Surface | null {
  const surface: Surface = { x: null, y: null };
  for (let node: Element | null = target; node !== null; node = node.parentElement) {
    const style = getComputedStyle(node);
    surface.x ??=
      SCROLLABLE.test(style.overflowX) && node.scrollWidth - node.clientWidth > 1 ? node : null;
    surface.y ??=
      SCROLLABLE.test(style.overflowY) && node.scrollHeight - node.clientHeight > 1 ? node : null;
    if (surface.x !== null && surface.y !== null) {
      break;
    }
  }
  return surface.x === null && surface.y === null ? null : surface;
}

/** Installs the app-wide middle-click autoscroll; returns a teardown. */
export function installMiddleClickAutoscroll(): () => void {
  let surface: Surface | null = null;
  let frame = 0;
  let lastFrame = 0;
  let originX = 0;
  let originY = 0;
  let pointerX = 0;
  let pointerY = 0;
  let movedWhileHeld = false;
  let held: AbortController | null = null;
  let marker: HTMLElement | null = null;
  const stop = (): void => {
    cancelAnimationFrame(frame);
    frame = 0;
    held?.abort();
    held = null;
    marker?.remove();
    marker = null;
    surface?.x?.classList.remove("middle-click-autoscrolling");
    surface?.y?.classList.remove("middle-click-autoscrolling");
    surface = null;
  };
  const animate = (time: number): void => {
    if (surface === null || !currentEditorOptions().middleClickAutoscroll) {
      stop();
      return;
    }
    if (lastFrame !== 0) {
      const step = (distance: number): number =>
        (Math.sign(distance) * Math.max(Math.abs(distance) - DEAD_ZONE, 0) * (time - lastFrame)) /
        32;
      if (surface.y !== null) {
        surface.y.scrollTop += step(pointerY - originY);
      }
      if (surface.x !== null) {
        surface.x.scrollLeft += step(pointerX - originX);
      }
    }
    lastFrame = time;
    frame = requestAnimationFrame(animate);
  };
  const consume = (event: MouseEvent): void => {
    event.preventDefault();
    event.stopPropagation();
  };
  const dragged = (x: number, y: number): boolean =>
    Math.abs(x - originX) > DEAD_ZONE || Math.abs(y - originY) > DEAD_ZONE;
  // A press that dismisses a running autoscroll is swallowed whole, as the engine's own gesture swallows it:
  // cancelling the press doesn't cancel the click behind it, which would activate whatever sits under the
  // pointer. Armed until that click arrives or the next press starts, so it can never eat an unrelated one.
  const swallowDismissal = (): void => {
    const swallow = new AbortController();
    const options = { capture: true, signal: swallow.signal };
    const eat = (click: MouseEvent): void => {
      consume(click);
      swallow.abort();
    };
    window.addEventListener("click", eat, options);
    window.addEventListener("auxclick", eat, options);
    window.addEventListener("mousedown", () => swallow.abort(), options);
  };
  const onMouseDown = (event: MouseEvent): void => {
    if (surface !== null) {
      consume(event);
      stop();
      swallowDismissal();
      return;
    }
    const target = event.target;
    if (
      event.button !== 1 ||
      event.altKey ||
      event.ctrlKey ||
      event.metaKey ||
      event.shiftKey ||
      !(target instanceof Element) ||
      target.closest(OWNS_MIDDLE_CLICK) !== null
    ) {
      return;
    }
    const found = surfaceAt(target);
    if (found === null) {
      return;
    }
    // Consumed before the setting is read: turning the setting off has to silence the engine's own autoscroll
    // (Chromium ships one) too, or "off" would mean something different on Windows.
    consume(event);
    if (!currentEditorOptions().middleClickAutoscroll) {
      return;
    }
    // Bound to the drag: a window-level wheel listener — passive or not — takes the page off WebKit's
    // async-scrolling path, so it must not outlive an active autoscroll.
    held = new AbortController();
    const heldOptions = { capture: true, signal: held.signal };
    window.addEventListener("mousemove", onMouseMove, heldOptions);
    window.addEventListener("mouseup", onMouseUp, heldOptions);
    window.addEventListener("keydown", onKeyDown, heldOptions);
    window.addEventListener("wheel", stop, heldOptions);
    window.addEventListener("blur", stop, { signal: held.signal });
    surface = found;
    originX = pointerX = event.clientX;
    originY = pointerY = event.clientY;
    movedWhileHeld = false;
    lastFrame = 0;
    marker = document.body.appendChild(document.createElement("div"));
    marker.className = "middle-click-autoscroll-origin";
    marker.style.left = `${event.clientX}px`;
    marker.style.top = `${event.clientY}px`;
    found.x?.classList.add("middle-click-autoscrolling");
    found.y?.classList.add("middle-click-autoscrolling");
    frame = requestAnimationFrame(animate);
  };
  const onMouseMove = (event: MouseEvent): void => {
    pointerX = event.clientX;
    pointerY = event.clientY;
    movedWhileHeld ||= dragged(pointerX, pointerY);
  };
  // Releasing after a real drag ends the scroll; releasing in place leaves it armed for a click-move-click.
  const onMouseUp = (event: MouseEvent): void => {
    if (event.button === 1 && (movedWhileHeld || dragged(event.clientX, event.clientY))) {
      consume(event);
      stop();
      swallowDismissal();
    }
  };
  // Escape ends the scroll and goes no further: the surface being scrolled often treats it as dismiss (the
  // file browser closes on it), and stopping a scroll shouldn't cost the user their panel.
  const onKeyDown = (event: KeyboardEvent): void => {
    if (event.key === "Escape") {
      event.preventDefault();
      event.stopPropagation();
      stop();
    }
  };
  const controller = new AbortController();
  document.addEventListener("mousedown", onMouseDown, {
    capture: true,
    signal: controller.signal,
  });
  return () => {
    controller.abort();
    stop();
  };
}
