// One gesture engine serves DOM panes and registered virtual scroll axes.
// Unregistered Monaco editors keep their own scrollOnMiddleClick contribution.

import { currentEditorOptions } from "../editor-options";
import { type MiddleClickScrollSurface, middleClickSurfaceAt } from "./middle-click-scroll-surface";

// Pointer travel that scrolls at zero speed, so a press without a deliberate drag holds still.
const DEAD_ZONE = 5;

/** Installs the app-wide middle-click autoscroll; returns a teardown. */
export function installMiddleClickAutoscroll(): () => void {
  let surface: MiddleClickScrollSurface | null = null;
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
    surface?.x?.element.classList.remove("middle-click-autoscrolling");
    surface?.y?.element.classList.remove("middle-click-autoscrolling");
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
      for (const direction of ["y", "x"] as const) {
        const axis = surface[direction];
        if (axis === null) continue;
        if (!axis.live || !axis.element.isConnected || axis.element.closest("[hidden],[inert]")) {
          axis.element.classList.remove("middle-click-autoscrolling");
          surface[direction] = null;
        } else {
          axis.scrollBy(step(direction === "y" ? pointerY - originY : pointerX - originX));
        }
      }
    }
    if (surface.x === null && surface.y === null) {
      stop();
      return;
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
      !(target instanceof Element)
    ) {
      return;
    }
    const enabled = currentEditorOptions().middleClickAutoscroll;
    // Disabled editor autoscroll gives Monaco its normal selection/PRIMARY-paste gesture back.
    if (!enabled && target.closest(".monaco-editor")) return;
    const found = middleClickSurfaceAt(target);
    if (found === null) {
      return;
    }
    // Suppress the engine's built-in autoscroll on DOM surfaces even when ours is disabled.
    consume(event);
    if (!enabled) {
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
    found.x?.element.classList.add("middle-click-autoscrolling");
    found.y?.element.classList.add("middle-click-autoscrolling");
    frame = requestAnimationFrame(animate);
  };
  const onMouseMove = (event: MouseEvent): void => {
    pointerX = event.clientX;
    pointerY = event.clientY;
    movedWhileHeld ||= dragged(pointerX, pointerY);
  };
  // Releasing after a real drag ends the scroll; releasing in place leaves it armed for a click-move-click.
  const onMouseUp = (event: MouseEvent): void => {
    if (event.button !== 1) return;
    consume(event);
    if (movedWhileHeld || dragged(event.clientX, event.clientY)) {
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
    }
    stop();
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
