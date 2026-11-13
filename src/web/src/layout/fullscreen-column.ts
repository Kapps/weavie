import { createSignal, onCleanup } from "solid-js";
import { fullscreenWidth, setFullscreenWidth } from "../chrome/rail-state";
import { createResizeDrag } from "../chrome/resize-drag";
import { currentEditorOptions, onEditorOptionsChanged } from "../editor-options";

const MIN_WIDTH = 480;
// Mirrors RailStateStore.FillFullscreenWidth: fill the pane area at any window size.
const FILL_WIDTH = 0;
const FILL_SNAP = 16;

/** Live fullscreen alignment and resizing, with geometry measured only at pointer down. */
export function createFullscreenColumn(): {
  style: () => string;
  start: (event: PointerEvent) => void;
} {
  const [center, setCenter] = createSignal(currentEditorOptions().fullscreenCenter);
  onCleanup(onEditorOptionsChanged((options) => setCenter(options.fullscreenCenter)));
  const drag = createResizeDrag({
    saved: fullscreenWidth,
    save: setFullscreenWidth,
    measure: (start) => {
      const handle = start.currentTarget as HTMLElement;
      const area = handle.parentElement!.getBoundingClientRect();
      const column = handle.parentElement!.querySelector(".layout-root")!.getBoundingClientRect();
      const midpoint = center() === "window" ? window.innerWidth / 2 : area.left + area.width / 2;
      const left = handle.classList.contains("left");
      return (move) => {
        const delta = move.clientX - start.clientX;
        const right = column.right + delta;
        // A clamped left edge cannot follow the pointer; shrink from the rendered width without a jump.
        const next = left
          ? column.width - 2 * delta
          : Math.min(2 * (right - midpoint), right - area.left);
        return next >= area.width - 2 * FILL_SNAP ? FILL_WIDTH : Math.max(MIN_WIDTH, next);
      };
    },
  });
  return {
    // The pane area's right edge is the window's right edge; its local window midpoint is 100% - 50vw.
    style: () =>
      `--fullscreen-width:${drag.size() === FILL_WIDTH ? "100%" : `${drag.size()}px`};` +
      `--fullscreen-center:${center() === "window" ? "calc(100% - 50vw)" : "50%"}`,
    start: drag.start,
  };
}
