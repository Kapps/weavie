import { type Accessor, createSignal } from "solid-js";
import { panelWidth, setPanelWidth } from "./rail-state";

const MIN_WIDTH = 200;
const MAX_WIDTH = 520;

/** Dragging the pinned panel's right edge: the live width while dragging, saved once the drag ends. */
export function createPanelResize(panel: () => HTMLElement | undefined): {
  width: Accessor<number>;
  start: (event: PointerEvent) => void;
} {
  const [dragWidth, setDragWidth] = createSignal<number | null>(null);
  const start = (event: PointerEvent): void => {
    if (event.button !== 0) return;
    const handle = event.currentTarget as HTMLElement;
    const left = panel()?.getBoundingClientRect().left ?? 0;
    handle.setPointerCapture(event.pointerId);
    const move = (next: PointerEvent): void => {
      setDragWidth(Math.round(Math.min(MAX_WIDTH, Math.max(MIN_WIDTH, next.clientX - left))));
    };
    // Lost capture follows pointerup and pointercancel alike, so every way a drag ends saves and cleans up.
    const end = (): void => {
      handle.removeEventListener("pointermove", move);
      handle.removeEventListener("lostpointercapture", end);
      const width = dragWidth();
      setDragWidth(null);
      if (width !== null) setPanelWidth(width);
    };
    handle.addEventListener("pointermove", move);
    handle.addEventListener("lostpointercapture", end);
  };
  return { width: () => dragWidth() ?? panelWidth(), start };
}
