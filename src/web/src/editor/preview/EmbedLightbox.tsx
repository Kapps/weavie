import { type JSX, Show, createEffect, createSignal, onCleanup, onMount } from "solid-js";
import { Portal } from "solid-js/web";
import type { EmbedZoomState } from "./embed-zoom";

// Zoom bounds: 1× is the fitted view (the floor — smaller is pointless), 8× is past where raster
// upscaling stops being useful.
const MAX_SCALE = 8;
const KEY_STEP = 1.25;

/**
 * Full-app lightbox for a preview embed: a Portal to body so it paints over every pane (terminal
 * included), showing a clone of the zoomed image / Mermaid diagram at viewport size. A capture-phase
 * listener owns Escape (close), the arrow keys (step) and +/-/0 (zoom) so they never reach the focused
 * editor/terminal (the earlier-registered keybinding resolver still runs first, as with every modal).
 * The wheel zooms toward the cursor, a zoomed embed pans by dragging, double-click toggles fit ↔ 2×,
 * and stepping to another embed resets to the fitted view.
 */
export function EmbedLightbox(props: {
  state: EmbedZoomState;
  onStep: (delta: number) => void;
  onClose: () => void;
}): JSX.Element {
  let frame!: HTMLDivElement;
  const [scale, setScale] = createSignal(1);
  const [offset, setOffset] = createSignal({ x: 0, y: 0 });
  const [panning, setPanning] = createSignal(false);
  // The active drag: pointer id, grab point, and the offset it started from. Null when not panning.
  let drag: { id: number; x: number; y: number; tx: number; ty: number } | null = null;

  // Back to the fitted view: identity transform, and any in-flight drag is abandoned with it so a
  // stale grab offset can never pan a 1× view.
  const reset = (): void => {
    drag = null;
    setPanning(false);
    setScale(1);
    setOffset({ x: 0, y: 0 });
  };

  // Rescales toward a viewport point so the content under it stays put; reaching 1× lands on the
  // fitted view.
  const zoomAt = (clientX: number, clientY: number, next: number): void => {
    const clamped = Math.min(MAX_SCALE, Math.max(1, next));
    if (clamped === 1) {
      reset();
      return;
    }
    const rect = frame.getBoundingClientRect();
    const cx = clientX - rect.left - rect.width / 2;
    const cy = clientY - rect.top - rect.height / 2;
    const ratio = clamped / scale();
    const t = offset();
    setOffset({ x: cx - ratio * (cx - t.x), y: cy - ratio * (cy - t.y) });
    setScale(clamped);
  };
  const zoomCenter = (factor: number): void => {
    const rect = frame.getBoundingClientRect();
    zoomAt(rect.left + rect.width / 2, rect.top + rect.height / 2, scale() * factor);
  };

  // Stepping to another embed (a new state object) starts it at the fitted view.
  createEffect(() => {
    props.state;
    reset();
  });

  const onKeyDown = (event: KeyboardEvent): void => {
    if (event.ctrlKey || event.metaKey || event.altKey) {
      return; // modifier chords (e.g. the global font zoom) aren't the lightbox's to consume
    }
    const actions: Record<string, () => void> = {
      Escape: () => props.onClose(),
      ArrowRight: () => props.onStep(1),
      ArrowLeft: () => props.onStep(-1),
      "+": () => zoomCenter(KEY_STEP),
      "=": () => zoomCenter(KEY_STEP),
      "-": () => zoomCenter(1 / KEY_STEP),
      "0": () => reset(),
    };
    const action = actions[event.key];
    if (action === undefined) {
      return;
    }
    event.preventDefault();
    event.stopPropagation();
    action();
  };
  onMount(() => window.addEventListener("keydown", onKeyDown, { capture: true }));
  onCleanup(() => window.removeEventListener("keydown", onKeyDown, { capture: true }));

  // Wheel = zoom (nothing else scrolls in a modal). Registered by hand so it's non-passive: the
  // preventDefault keeps the gesture from ever reaching the app beneath (page zoom, terminal wheel).
  const onWheel = (event: WheelEvent): void => {
    event.preventDefault();
    event.stopPropagation();
    const delta = event.deltaMode === WheelEvent.DOM_DELTA_LINE ? event.deltaY * 33 : event.deltaY;
    zoomAt(event.clientX, event.clientY, scale() * Math.exp(-delta * 0.0015));
  };

  const onPointerDown = (event: PointerEvent & { currentTarget: HTMLDivElement }): void => {
    // Clicks inside never dismiss via the backdrop; a zoomed embed additionally starts a pan.
    event.stopPropagation();
    if (scale() === 1) {
      return;
    }
    const t = offset();
    drag = { id: event.pointerId, x: event.clientX, y: event.clientY, tx: t.x, ty: t.y };
    event.currentTarget.setPointerCapture(event.pointerId);
    setPanning(true);
  };
  const onPointerMove = (event: PointerEvent): void => {
    if (drag === null || event.pointerId !== drag.id) {
      return;
    }
    setOffset({ x: drag.tx + event.clientX - drag.x, y: drag.ty + event.clientY - drag.y });
  };
  const onPointerUp = (event: PointerEvent): void => {
    if (drag?.id === event.pointerId) {
      drag = null;
      setPanning(false);
    }
  };
  const onDblClick = (event: MouseEvent): void => {
    if (scale() > 1) {
      reset();
    } else {
      zoomAt(event.clientX, event.clientY, 2);
    }
  };

  const hint = (): string => {
    const parts: string[] = [];
    if (props.state.targets.length > 1) {
      parts.push(`${props.state.index + 1} / ${props.state.targets.length} (←/→)`);
    }
    if (scale() > 1) {
      parts.push(`${scale().toFixed(1)}× — drag to pan, 0 resets`);
    }
    return parts.join(" · ");
  };

  return (
    <Portal>
      <div
        class="embed-lightbox"
        role="dialog"
        aria-modal="true"
        aria-label="Zoomed embed"
        onPointerDown={() => props.onClose()}
        ref={(el) => el.addEventListener("wheel", onWheel, { passive: false })}
      >
        <div
          class="embed-lightbox-body"
          classList={{ zoomed: scale() > 1, panning: panning() }}
          ref={frame}
          onPointerDown={onPointerDown}
          onPointerMove={onPointerMove}
          onPointerUp={onPointerUp}
          onPointerCancel={onPointerUp}
          onDblClick={onDblClick}
        >
          {/* String-valued style: the style-object form compiles to setStyleProperty, which this
              solid-js runtime doesn't export (version skew with the JSX transform). */}
          <div
            class="embed-lightbox-zoom"
            style={`transform: translate(${offset().x}px, ${offset().y}px) scale(${scale()})`}
          >
            {/* The index is always in range: opening sets a hit and stepping wraps. */}
            {zoomClone(props.state.targets[props.state.index]!)}
          </div>
        </div>
        <Show when={hint().length > 0}>
          <div class="embed-lightbox-count">{hint()}</div>
        </Show>
      </div>
    </Portal>
  );
}

// A display clone of the embed, stripped of the in-preview dressing: the magnifier button (a Mermaid
// wrapper carries it as a child) and Mermaid's inline size caps, so the vector can fill the lightbox.
function zoomClone(target: HTMLElement): HTMLElement {
  const clone = target.cloneNode(true) as HTMLElement;
  clone.classList.remove("embed-zoom");
  clone.querySelector(".embed-zoom-btn")?.remove();
  const svg = clone.querySelector("svg");
  if (svg !== null) {
    svg.style.maxWidth = "";
    svg.removeAttribute("width");
    svg.removeAttribute("height");
  }
  return clone;
}
