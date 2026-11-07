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

/** What the wheel and scrolling keys drive: the pane owns the scroll position they move. */
interface AgentPaneWheelTarget {
  /** Moves toward the latest output by `delta` pixels; returns the edge the move reached. */
  scrollBy(delta: number): "start" | "end" | null;
  intent(delta: number): void;
  settled(): void;
  start(): void;
  end(): void;
  lineHeight(): number;
  viewportHeight(): number;
}

export function createAgentPaneWheel(
  body: Accessor<HTMLDivElement | undefined>,
  pane: AgentPaneWheelTarget,
) {
  let frame: number | null = null;
  let remaining = 0;
  let distance = 0;
  let started = 0;
  let progress = 0;

  const cancel = (): void => {
    if (frame !== null) cancelAnimationFrame(frame);
    frame = null;
    remaining = 0;
  };

  const move = (delta: number): void => {
    const edge = pane.scrollBy(delta);
    if (edge !== null) remaining = 0;
    // History measured on arrival extends the top, so reaching it hands off to the start edge.
    if (edge === "start") pane.start();
  };

  const animate = (time: number): void => {
    const next = 1 - (1 - Math.max(0, Math.min(1, (time - started) / animationDuration))) ** 3;
    const delta = distance * (next - progress);
    progress = next;
    remaining -= delta;
    move(delta);
    if (next === 1 || remaining === 0) {
      cancel();
      pane.settled();
    } else {
      frame = requestAnimationFrame(animate);
    }
  };

  const wheel = (event: WheelEvent): void => {
    const element = body()!;
    if (
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
        ? pane.lineHeight()
        : event.deltaMode === WheelEvent.DOM_DELTA_PAGE
          ? pane.viewportHeight()
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
    scroll(delta, !precisePixel && classifier.isPhysicalMouseWheel());
  };

  const scroll = (delta: number, animated: boolean): void => {
    pane.intent(delta);
    if (!animated || !currentEditorOptions().smoothScrolling) {
      let movement = delta;
      if (frame !== null) {
        if (Math.sign(delta) === Math.sign(remaining)) movement += remaining;
        cancel();
      }
      move(movement);
      pane.settled();
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
    const element = body()!;
    const interrupt = (): void => {
      const active = frame !== null;
      cancel();
      if (active) pane.settled();
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
      const viewport = pane.viewportHeight();
      const page = Math.max(viewport * 0.875, viewport - lineStep);
      const step = {
        ArrowUp: -lineStep,
        ArrowDown: lineStep,
        PageUp: -page,
        PageDown: page,
        " ": event.shiftKey ? -page : page,
      }[event.key];
      const edge = { Home: pane.start, End: pane.end }[event.key];
      if (edge === undefined && (step === undefined || event.ctrlKey)) return;
      event.preventDefault();
      if (edge !== undefined) edge();
      else scroll(step!, true);
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
