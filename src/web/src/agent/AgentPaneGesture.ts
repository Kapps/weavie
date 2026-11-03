import { type Accessor, onCleanup, onMount } from "solid-js";
import { notify } from "../notify/notify";

/**
 * Tracks a touch gesture on the pane from touchstart until its fingers lift or, when a finger scrolled the pane,
 * until the `scrollend` that follows its momentum. Each finger's touchstart pairs with its touchend or touchcancel,
 * and a scroll the user drove always ends in `scrollend`, so the gesture always ends.
 */
export function createAgentPaneGesture(
  body: Accessor<HTMLDivElement | undefined>,
  onChanged: () => void,
) {
  // The fingers that went down on the pane, which are the ones it hears lift.
  const fingers = new Set<number>();
  // A finger moved since the last scroll event, so the next one is the finger's.
  let dragged = false;
  let momentum = false;
  let active = false;

  const update = (): void => {
    const next = fingers.size > 0 || momentum;
    if (next === active) return;
    active = next;
    onChanged();
  };

  onMount(() => {
    const element = body()!;
    const tracksMomentum = "onscrollend" in element;
    const touch = (event: TouchEvent): void => {
      const lifted = event.type === "touchend" || event.type === "touchcancel";
      if (event.type === "touchstart" && fingers.size === 0) dragged = false;
      for (const changed of event.changedTouches) {
        if (event.type === "touchstart") fingers.add(changed.identifier);
        else if (lifted) fingers.delete(changed.identifier);
      }
      if (event.type === "touchmove") dragged = true;
      if (event.type === "touchstart" && !tracksMomentum) {
        notify(
          "error",
          "This browser doesn't report when scrolling ends, so the agent pane can't hold output still during touch momentum.",
          "agent-pane-scrollend",
        );
      }
      update();
    };
    const scrolled = (): void => {
      if (dragged && fingers.size > 0 && tracksMomentum) momentum = true;
      dragged = false;
    };
    const ended = (): void => {
      momentum = false;
      update();
    };
    const types = ["touchstart", "touchmove", "touchend", "touchcancel"] as const;
    for (const type of types) element.addEventListener(type, touch, { passive: true });
    element.addEventListener("scroll", scrolled, { passive: true });
    element.addEventListener("scrollend", ended);
    onCleanup(() => {
      for (const type of types) element.removeEventListener(type, touch);
      element.removeEventListener("scroll", scrolled);
      element.removeEventListener("scrollend", ended);
    });
  });

  return {
    /** Whether a touch gesture, including its momentum, is in progress. */
    touching: (): boolean => active,
    /** Forgets momentum the pane has just stopped by taking over the scroll position. */
    reset(): void {
      momentum = false;
      update();
    },
  };
}
