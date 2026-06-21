import { For, type JSX, Show } from "solid-js";
import { type ResizeEdge, postToHost } from "../bridge";

// Resize grab handles for the frameless host window (Windows custom chrome). A single WebView2 fills the
// frameless client area and covers the OS resize border, so WM_NCHITTEST never fires at the edges. These thin
// handles at the window border ask the host to begin a native OS resize from that edge on left-mousedown.
// Hidden while maximized. Only rendered on Windows custom chrome (gated by the caller).
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
                // Left button only; let other buttons fall through.
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
