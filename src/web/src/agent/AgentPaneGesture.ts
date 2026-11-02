import { type Accessor, onCleanup, onMount } from "solid-js";

/** Tracks scrolling the reader drives by touch or pointer, through any momentum, until `scrollend`. */
export function createAgentPaneGesture(
  body: Accessor<HTMLDivElement | undefined>,
  onTouchChanged: () => void,
) {
  let touches = 0;
  let pressed = false;
  let moving = false;
  let touchDriven = false;

  const settle = (): void => {
    if (touches > 0 || pressed || moving) return;
    if (touchDriven) {
      touchDriven = false;
      onTouchChanged();
    }
  };

  onMount(() => {
    const element = body()!;
    const touch = (event: TouchEvent): void => {
      touches = event.touches.length;
      if (event.type === "touchstart") {
        touchDriven = true;
        onTouchChanged();
      }
      settle();
    };
    const release = (): void => {
      pressed = false;
      settle();
    };
    const press = (event: PointerEvent): void => {
      if (event.pointerType === "touch") return;
      pressed = true;
      touchDriven = false;
      for (const type of ["pointerup", "pointercancel"]) {
        window.addEventListener(type, release, { once: true, capture: true });
      }
    };
    const ended = (): void => {
      moving = false;
      settle();
    };
    const options = { passive: true };
    for (const type of ["touchstart", "touchend", "touchcancel"] as const) {
      element.addEventListener(type, touch, options);
    }
    element.addEventListener("pointerdown", press, options);
    element.addEventListener("scrollend", ended);
    onCleanup(() => {
      for (const type of ["touchstart", "touchend", "touchcancel"] as const) {
        element.removeEventListener(type, touch);
      }
      element.removeEventListener("pointerdown", press);
      element.removeEventListener("scrollend", ended);
      for (const type of ["pointerup", "pointercancel"]) {
        window.removeEventListener(type, release, { capture: true });
      }
    });
  });

  return {
    /** Whether the current scroll is the reader's own: held, released into momentum, or autoscrolling. */
    active: (): boolean =>
      touches > 0 ||
      pressed ||
      moving ||
      body()?.classList.contains("middle-click-autoscrolling") === true,
    /** Whether a touch gesture, including its momentum, is still in progress. */
    touching: (): boolean => touchDriven,
    noteScroll(): void {
      if (touches > 0 || pressed) moving = true;
    },
    /** Forgets momentum the pane has just stopped by taking over the scroll position. */
    reset(): void {
      moving = false;
      touchDriven = touches > 0;
    },
  };
}
