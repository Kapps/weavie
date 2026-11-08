import { SWIPE_INTENT_DISTANCE } from "../../mobile/mobile-back-swipe";

// Fling decay per millisecond, and the speed (px/ms) below which a fling stops.
const FRICTION = 0.997;
const MIN_SPEED = 0.02;
// Only the drag's last moments decide a fling's speed.
const VELOCITY_WINDOW_MS = 100;

/**
 * Drags the review vertically under one finger and flings it on release. The review scrolls by transform, so the
 * browser has nothing to pan natively; horizontal drags stay native and a tap never moves, so taps still click.
 */
export function registerTouchScroll(
  element: HTMLElement,
  owns: (target: Element) => boolean,
  scrollBy: (delta: number) => void,
): () => void {
  let touch: { id: number; x: number; y: number; dragging: boolean } | null = null;
  let samples: { time: number; y: number }[] = [];
  let fling = 0;
  const stopFling = (): void => cancelAnimationFrame(fling);
  const start = (event: TouchEvent): void => {
    stopFling();
    const only = event.touches.length === 1 ? event.touches[0]! : null;
    const first = only?.target instanceof Element && owns(only.target) ? only : null;
    touch =
      first === null
        ? null
        : { id: first.identifier, x: first.clientX, y: first.clientY, dragging: false };
    samples = first === null ? [] : [{ time: event.timeStamp, y: first.clientY }];
  };
  const move = (event: TouchEvent): void => {
    const current =
      touch === null ? undefined : [...event.touches].find((t) => t.identifier === touch!.id);
    if (touch === null || current === undefined || event.touches.length !== 1) {
      touch = null;
      return;
    }
    if (!touch.dragging) {
      const dx = Math.abs(current.clientX - touch.x);
      const dy = Math.abs(current.clientY - touch.y);
      if (dy < SWIPE_INTENT_DISTANCE || dy <= dx) {
        if (dx >= SWIPE_INTENT_DISTANCE) touch = null;
        return;
      }
      touch.dragging = true;
    }
    event.preventDefault();
    scrollBy(touch.y - current.clientY);
    touch.y = current.clientY;
    samples.push({ time: event.timeStamp, y: current.clientY });
    samples = samples.filter((sample) => event.timeStamp - sample.time <= VELOCITY_WINDOW_MS);
  };
  const end = (event: TouchEvent): void => {
    const dragged = touch?.dragging === true;
    touch = null;
    const first = samples[0];
    const last = samples.at(-1);
    if (!dragged || first === undefined || last === undefined || event.timeStamp - last.time > 50)
      return;
    let speed = (first.y - last.y) / Math.max(1, last.time - first.time);
    let previous: number | null = null;
    const step = (now: number): void => {
      const elapsed = previous === null ? 0 : now - previous;
      previous = now;
      speed *= FRICTION ** elapsed;
      if (Math.abs(speed) < MIN_SPEED) return;
      scrollBy(speed * elapsed);
      fling = requestAnimationFrame(step);
    };
    fling = requestAnimationFrame(step);
  };
  element.addEventListener("touchstart", start, { passive: true });
  element.addEventListener("touchmove", move, { passive: false });
  element.addEventListener("touchend", end);
  element.addEventListener("touchcancel", start);
  return () => {
    stopFling();
    element.removeEventListener("touchstart", start);
    element.removeEventListener("touchmove", move);
    element.removeEventListener("touchend", end);
    element.removeEventListener("touchcancel", start);
  };
}
