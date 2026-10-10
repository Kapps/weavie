// Pan/zoom for a centered, fitted view (embed lightbox, media tabs): wheel and pinch zoom toward the
// gesture, a zoomed view pans by dragging, double-click toggles fit ↔ 2×.

import { createSignal } from "solid-js";

// 1× is the fitted view and the floor; 8× is past where raster upscaling stops being useful.
const MAX_SCALE = 8;
const KEY_STEP = 1.25;
// A press must travel this far before it pans, so clicks and taps still reach the content (video controls).
const DRAG_SLOP = 4;

/** Pan/zoom over `frame`, whose content is centered at 1×: bind `handlers` to it and `transform()` to its content. */
export function createPanZoom(frame: () => HTMLElement) {
  const [scale, setScale] = createSignal(1);
  const [offset, setOffset] = createSignal({ x: 0, y: 0 });
  const [panning, setPanning] = createSignal(false);
  // Active pointers by id; one pans a zoomed view, two pinch. `start` snapshots the gesture's origin.
  const pointers = new Map<number, { x: number; y: number }>();
  let start: { scale: number; tx: number; ty: number; x: number; y: number; dist: number } | null =
    null;

  const center = (): { x: number; y: number } => {
    const [a, b] = [...pointers.values()];
    return b === undefined ? a! : { x: (a!.x + b.x) / 2, y: (a!.y + b.y) / 2 };
  };
  const spread = (): number => {
    const [a, b] = [...pointers.values()];
    return b === undefined ? 0 : Math.hypot(a!.x - b.x, a!.y - b.y);
  };
  const begin = (): void => {
    const c = center();
    const t = offset();
    start = { scale: scale(), tx: t.x, ty: t.y, x: c.x, y: c.y, dist: spread() };
  };

  const reset = (): void => {
    pointers.clear();
    start = null;
    setPanning(false);
    setScale(1);
    setOffset({ x: 0, y: 0 });
  };

  // Rescales toward a viewport point so the content under it stays put; reaching 1× lands on the fit.
  const zoomAt = (clientX: number, clientY: number, next: number): void => {
    const clamped = Math.min(MAX_SCALE, Math.max(1, next));
    if (clamped === 1) {
      setScale(1);
      setOffset({ x: 0, y: 0 });
      return;
    }
    const rect = frame().getBoundingClientRect();
    const cx = clientX - rect.left - rect.width / 2;
    const cy = clientY - rect.top - rect.height / 2;
    const ratio = clamped / scale();
    const t = offset();
    setOffset({ x: cx - ratio * (cx - t.x), y: cy - ratio * (cy - t.y) });
    setScale(clamped);
  };
  const zoomBy = (factor: number): void => {
    const rect = frame().getBoundingClientRect();
    zoomAt(rect.left + rect.width / 2, rect.top + rect.height / 2, scale() * factor);
  };

  const release = (event: PointerEvent): void => {
    if (!pointers.delete(event.pointerId)) {
      return;
    }
    // A pinch dropping to one finger re-anchors so the remaining finger pans from where it is.
    if (pointers.size > 0) {
      begin();
    } else {
      start = null;
      setPanning(false);
    }
  };

  return {
    scale,
    panning,
    transform: () => `translate(${offset().x}px, ${offset().y}px) scale(${scale()})`,
    hint: () => (scale() > 1 ? `${scale().toFixed(1)}× — drag to pan, 0 resets` : ""),
    reset,
    zoomBy,
    /** Applies the shared +/-/0 zoom keys; returns whether `key` was one of them. */
    zoomKey(key: string): boolean {
      const actions: Record<string, () => void> = {
        "+": () => zoomBy(KEY_STEP),
        "=": () => zoomBy(KEY_STEP),
        "-": () => zoomBy(1 / KEY_STEP),
        "0": reset,
      };
      actions[key]?.();
      return key in actions;
    },
    handlers: {
      onPointerDown(event: PointerEvent & { currentTarget: HTMLElement }): void {
        if (pointers.size >= 2 || (event.pointerType === "mouse" && scale() === 1)) {
          return;
        }
        // A lone press is only remembered until it drags (or a second finger pinches), so taps and
        // clicks still reach the content.
        pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
        if (pointers.size === 2) {
          event.currentTarget.setPointerCapture(event.pointerId);
        }
        begin();
      },
      onPointerMove(event: PointerEvent & { currentTarget: HTMLElement }): void {
        if (start === null || !pointers.has(event.pointerId)) {
          return;
        }
        pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
        const c = center();
        if (pointers.size === 2 && start.dist > 0) {
          setScale(start.scale);
          setOffset({ x: start.tx + c.x - start.x, y: start.ty + c.y - start.y });
          zoomAt(c.x, c.y, start.scale * (spread() / start.dist));
        } else if (scale() > 1) {
          if (!panning()) {
            if (Math.hypot(c.x - start.x, c.y - start.y) < DRAG_SLOP) return;
            event.currentTarget.setPointerCapture(event.pointerId);
            setPanning(true);
          }
          setOffset({ x: start.tx + c.x - start.x, y: start.ty + c.y - start.y });
        }
      },
      onPointerUp: release,
      onPointerCancel: release,
      onPointerLeave: release,
      // An <img>'s native drag-and-drop would cancel the pointer mid-pan.
      onDragStart: (event: DragEvent) => event.preventDefault(),
      onDblClick(event: MouseEvent): void {
        if (scale() > 1) {
          reset();
        } else {
          zoomAt(event.clientX, event.clientY, 2);
        }
      },
    },
    /** Non-passive wheel listener: zooms toward the cursor and keeps the gesture from reaching the page. */
    onWheel(event: WheelEvent): void {
      event.preventDefault();
      event.stopPropagation();
      const delta =
        event.deltaMode === WheelEvent.DOM_DELTA_LINE ? event.deltaY * 33 : event.deltaY;
      zoomAt(event.clientX, event.clientY, scale() * Math.exp(-delta * 0.0015));
    },
  };
}
