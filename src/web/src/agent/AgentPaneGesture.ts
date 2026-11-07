import { type Accessor, onCleanup, onMount } from "solid-js";
import { notify } from "../notify/notify";

/**
 * Tracks a touch gesture on the pane from touchstart until its fingers lift or, when a finger scrolled the pane,
 * until the `scrollend` that follows its momentum. Each finger's touchstart pairs with its touchend or touchcancel,
 * and only a scroll the finger drove, which always ends in `scrollend`, holds past the lift: one the layout
 * caused (a clamp, which sends no `scrollend`) never does, so the gesture always ends.
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

  const tracksMomentum = (): boolean => "onscrollend" in body()!;
  const start = (event: TouchEvent): void => {
    if (fingers.size === 0) dragged = false;
    for (const touch of event.changedTouches) fingers.add(touch.identifier);
    if (!tracksMomentum()) {
      notify(
        "error",
        "This browser doesn't report when scrolling ends, so the agent pane can't hold output still during touch momentum.",
        "agent-pane-scrollend",
      );
    }
    update();
  };
  const drag = (): void => {
    dragged = true;
  };
  const lift = (event: TouchEvent): void => {
    for (const touch of event.changedTouches) fingers.delete(touch.identifier);
    update();
  };
  const ended = (): void => {
    momentum = false;
    update();
  };

  onMount(() => {
    const element = body()!;
    const listening = new AbortController();
    const options = { passive: true, signal: listening.signal };
    element.addEventListener("touchstart", start, options);
    element.addEventListener("touchmove", drag, options);
    element.addEventListener("touchend", lift, options);
    element.addEventListener("touchcancel", lift, options);
    element.addEventListener("scrollend", ended, options);
    onCleanup(() => listening.abort());
  });

  return {
    /** Whether a touch gesture, including its momentum, is in progress. */
    touching: (): boolean => active,
    /** A scroll event; `byFinger` says, when asked, whether the layout couldn't have caused it. */
    scrolled(byFinger: () => boolean): void {
      if (dragged && fingers.size > 0 && tracksMomentum() && !momentum && byFinger()) {
        momentum = true;
      }
      dragged = false;
    },
    /** Forgets momentum the pane has just stopped by taking over the scroll position. */
    reset: ended,
  };
}
