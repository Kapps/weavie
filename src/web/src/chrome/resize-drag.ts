import { type Accessor, createSignal } from "solid-js";

/** Dragging a resize edge, across or down: the live size while dragging, saved once the drag ends. */
export function createResizeDrag(options: {
  saved: Accessor<number>;
  save: (size: number) => void;
  // Measures the layout as a drag starts, returning the size each later pointer position asks for.
  measure: (start: PointerEvent) => (move: PointerEvent) => number;
}): { size: Accessor<number>; start: (event: PointerEvent) => void } {
  const [dragSize, setDragSize] = createSignal<number | null>(null);
  const start = (event: PointerEvent): void => {
    if (event.button !== 0) return;
    // Cancelling the press keeps focus (and text selection) where it was.
    event.preventDefault();
    const handle = event.currentTarget as HTMLElement;
    const sizeAt = options.measure(event);
    handle.setPointerCapture(event.pointerId);
    const move = (next: PointerEvent): void => {
      setDragSize(Math.round(sizeAt(next)));
    };
    // Lost capture follows pointerup and pointercancel alike, so every way a drag ends saves and cleans up.
    const end = (): void => {
      handle.removeEventListener("pointermove", move);
      handle.removeEventListener("lostpointercapture", end);
      const size = dragSize();
      setDragSize(null);
      if (size !== null) options.save(size);
    };
    handle.addEventListener("pointermove", move);
    handle.addEventListener("lostpointercapture", end);
  };
  return { size: () => dragSize() ?? options.saved(), start };
}
