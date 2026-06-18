import { For, type JSX, Show } from "solid-js";
import { type ResizeEdge, postToHost } from "../bridge";

// Resize grab handles for the frameless host window (Windows custom chrome). The host window is frameless
// and a single WebView2 fills its client area, so it covers the OS resize border and the host's WM_NCHITTEST
// never fires at the edges. Instead we draw thin handles at the window border and, on left-mousedown, ask the
// host to begin a *native* OS resize from that edge — the same "web drives the window" handoff the title bar
// uses for dragging. The handles carry the matching resize cursor (set in styles.css) for hover feedback and
// are `app-region: no-drag` so WebView2 doesn't treat them as the draggable caption. Hidden while maximized,
// where the window can't be resized. Only rendered on Windows custom chrome (gated by the caller).
const EDGES: ResizeEdge[] = [
  "top",
  "bottom",
  "left",
  "right",
  "top-left",
  "top-right",
  "bottom-left",
  "bottom-right",
];

export function ResizeFrame(props: { maximized: boolean }): JSX.Element {
  return (
    <Show when={!props.maximized}>
      <div class="resize-frame" aria-hidden="true">
        <For each={EDGES}>
          {(edge) => (
            <div
              class={`resize-handle resize-${edge}`}
              onMouseDown={(e) => {
                // Left button only; let other buttons fall through (e.g. right-click system menu).
                if (e.button !== 0) {
                  return;
                }
                e.preventDefault();
                postToHost({ type: "window-resize", edge });
              }}
            />
          )}
        </For>
      </div>
    </Show>
  );
}
