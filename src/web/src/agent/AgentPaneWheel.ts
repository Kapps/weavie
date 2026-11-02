import {
  type IMouseWheelEvent,
  StandardWheelEvent,
} from "@codingame/monaco-vscode-api/vscode/vs/base/browser/mouseEvent";
import { MouseWheelClassifier } from "@codingame/monaco-vscode-api/vscode/vs/base/browser/ui/scrollbar/scrollableElement";
import { type Accessor, onCleanup, onMount } from "solid-js";
import { currentEditorOptions, onEditorOptionsChanged } from "../editor-options";

const animationDuration = 125;
// Chromium's keyboard steps: a line is 40px and a page keeps at most 40px of overlap.
const lineStep = 40;
const editable = "input, textarea, select, [contenteditable]:not([contenteditable='false'])";

export function createAgentPaneWheel(
  body: Accessor<HTMLDivElement | undefined>,
  onIntent: (delta: number) => void,
  onSettled: () => void,
  edges: { start: () => void; end: () => void },
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

  // The pane is bottom-origin, so scrollTop runs from minus the overflow up to 0.
  const move = (element: HTMLElement, delta: number): void => {
    const minimum = Math.min(0, element.clientHeight - element.scrollHeight);
    const target = Math.max(minimum, Math.min(0, element.scrollTop + delta + fraction));
    element.scrollTop = target;
    fraction = target - element.scrollTop;
    if ((delta < 0 && target === minimum) || (delta > 0 && target === 0)) remaining = 0;
    // History measured on arrival extends the top, so reaching it hands off to the start edge.
    if (delta < 0 && target === minimum && minimum < 0) edges.start();
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
    const classifier = MouseWheelClassifier.INSTANCE;
    classifier.acceptStandardWheelEvent(
      new StandardWheelEvent(event as WheelEvent & IMouseWheelEvent),
    );
    const precisePixel =
      event.deltaMode === WheelEvent.DOM_DELTA_PIXEL &&
      (Math.abs(event.deltaY) <= 1 || !Number.isInteger(event.deltaY));
    event.preventDefault();
    // Single-pixel and fractional input stay precise inside the classifier's near-integer tolerance.
    scroll(element, delta, !precisePixel && classifier.isPhysicalMouseWheel());
  };

  const scroll = (element: HTMLElement, delta: number, animated: boolean): void => {
    onIntent(delta);
    if (!animated || !currentEditorOptions().smoothScrolling) {
      let movement = delta;
      if (frame !== null) {
        if (Math.sign(delta) === Math.sign(remaining)) movement += remaining + fraction;
        cancel();
      }
      move(element, movement);
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
      const active = frame !== null;
      cancel();
      if (active) onSettled();
    };
    // Keys scroll here rather than natively, so their motion is the reader's like the wheel's.
    const keydown = (event: KeyboardEvent): void => {
      const target = event.target as HTMLElement;
      if (
        event.defaultPrevented ||
        event.altKey ||
        event.metaKey ||
        (target !== element && (event.key === " " || target.closest(editable) !== null))
      )
        return;
      const page = Math.max(element.clientHeight * 0.875, element.clientHeight - lineStep);
      const step = {
        ArrowUp: -lineStep,
        ArrowDown: lineStep,
        PageUp: -page,
        PageDown: page,
        " ": event.shiftKey ? -page : page,
      }[event.key];
      const edge = { Home: edges.start, End: edges.end }[event.key];
      if (edge === undefined && (step === undefined || event.ctrlKey)) return;
      event.preventDefault();
      if (edge !== undefined) edge();
      else scroll(element, step!, true);
      // The focused row may scroll out and unmount; the pane keeps the keys.
      element.focus({ preventScroll: true });
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
