import { type Accessor, onCleanup, onMount } from "solid-js";
import { currentEditorOptions, onEditorOptionsChanged } from "../editor-options";

const animationDuration = 125;
const scrollingKeys = new Set(["ArrowUp", "ArrowDown", "PageUp", "PageDown", "Home", "End", " "]);

export function createAgentPaneWheel(
  body: Accessor<HTMLDivElement | undefined>,
  onIntent: () => void,
  onSettled: () => void,
) {
  let frame: number | null = null;
  let remaining = 0;
  let distance = 0;
  let started = 0;
  let progress = 0;
  let fraction = 0;

  const cancel = (): void => {
    if (frame !== null) cancelAnimationFrame(frame);
    frame = null;
    remaining = 0;
    fraction = 0;
  };

  const move = (element: HTMLElement, delta: number): void => {
    const previous = element.scrollTop;
    const maximum = Math.max(0, element.scrollHeight - element.clientHeight);
    const target = Math.max(0, Math.min(maximum, previous + delta + fraction));
    element.scrollTop = target;
    fraction = target - element.scrollTop;
    if ((delta < 0 && target === 0) || (delta > 0 && target === maximum)) remaining = 0;
  };

  const animate = (time: number): void => {
    const element = body();
    if (element === undefined) {
      cancel();
      return;
    }
    const next = 1 - (1 - Math.max(0, Math.min(1, (time - started) / animationDuration))) ** 3;
    const delta = distance * (next - progress);
    progress = next;
    remaining -= delta;
    // Measurement anchoring changes scrollTop, but never spends the wheel's remaining distance.
    move(element, delta);
    if (next === 1 || remaining === 0) {
      cancel();
      onSettled();
    } else {
      frame = requestAnimationFrame(animate);
    }
  };

  const wheel = (event: WheelEvent): void => {
    const element = body();
    if (
      element === undefined ||
      event.defaultPrevented ||
      !event.cancelable ||
      event.ctrlKey ||
      event.metaKey ||
      event.shiftKey ||
      event.deltaY === 0 ||
      Math.abs(event.deltaX) > Math.abs(event.deltaY)
    )
      return;
    for (const target of event.composedPath()) {
      if (target === element) break;
      if (!(target instanceof HTMLElement)) continue;
      const style = getComputedStyle(target);
      if (!/^(auto|scroll)$/.test(style.overflowY)) continue;
      const canScroll =
        event.deltaY < 0
          ? target.scrollTop > 0
          : target.scrollTop + target.clientHeight < target.scrollHeight;
      if (canScroll || style.overscrollBehaviorY !== "auto") return;
    }
    const unit =
      event.deltaMode === WheelEvent.DOM_DELTA_LINE
        ? Number.parseFloat(getComputedStyle(element).lineHeight)
        : event.deltaMode === WheelEvent.DOM_DELTA_PAGE
          ? element.clientHeight
          : 1;
    const delta = event.deltaY * unit;
    event.preventDefault();
    onIntent();
    if (!currentEditorOptions().smoothScrolling) {
      cancel();
      move(element, delta);
      onSettled();
      return;
    }
    if (Math.sign(delta) !== Math.sign(remaining)) cancel();
    remaining += delta;
    distance = remaining;
    started = performance.now();
    progress = 0;
    if (frame === null) frame = requestAnimationFrame(animate);
  };

  onMount(() => {
    const element = body();
    if (element === undefined) return;
    const interrupt = (): void => {
      if (frame === null) return;
      cancel();
      onSettled();
    };
    const keydown = (event: KeyboardEvent): void => {
      if (event.target === element && !event.defaultPrevented && scrollingKeys.has(event.key))
        interrupt();
    };
    element.addEventListener("wheel", wheel, { passive: false });
    element.addEventListener("pointerdown", interrupt);
    element.addEventListener("keydown", keydown);
    const unsubscribe = onEditorOptionsChanged(interrupt);
    onCleanup(() => {
      cancel();
      unsubscribe();
      element.removeEventListener("wheel", wheel);
      element.removeEventListener("pointerdown", interrupt);
      element.removeEventListener("keydown", keydown);
    });
  });

  return { cancel, isActive: (): boolean => frame !== null };
}
