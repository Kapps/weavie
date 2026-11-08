import { type Accessor, createSignal } from "solid-js";

/** Dragging a resize edge: the live width while dragging, saved once the drag ends. */
export function createWidthDrag(options: {
  saved: Accessor<number>;
  save: (width: number) => void;
  // Measures the layout as a drag starts, returning the width each later pointer position asks for.
  measure: (start: PointerEvent) => (move: PointerEvent) => number;
}): { width: Accessor<number>; start: (event: PointerEvent) => void } {
  const [dragWidth, setDragWidth] = createSignal<number | null>(null);
  const start = (event: PointerEvent): void => {
    if (event.button !== 0) return;
    // Cancelling the press keeps focus (and text selection) where it was.
    event.preventDefault();
    const handle = event.currentTarget as HTMLElement;
    const widthAt = options.measure(event);
    handle.setPointerCapture(event.pointerId);
    const move = (next: PointerEvent): void => {
      setDragWidth(Math.round(widthAt(next)));
    };
    // Lost capture follows pointerup and pointercancel alike, so every way a drag ends saves and cleans up.
    const end = (): void => {
      handle.removeEventListener("pointermove", move);
      handle.removeEventListener("lostpointercapture", end);
      const width = dragWidth();
      setDragWidth(null);
      if (width !== null) options.save(width);
    };
    handle.addEventListener("pointermove", move);
    handle.addEventListener("lostpointercapture", end);
  };
  return { width: () => dragWidth() ?? options.saved(), start };
}
