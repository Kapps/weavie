import { For, type JSX, Show } from "solid-js";
import { postToLocalHost, type ResizeEdge } from "../bridge";

// Resize grab handles for the frameless host window (Windows custom chrome): the WebView2 covers the OS
// resize border so WM_NCHITTEST never fires at the edges, so these thin border handles ask the host to begin
// a native resize on left-mousedown. Hidden while maximized; gated by the caller.
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
                postToLocalHost({ type: "window-resize", edge });
              }}
            />
          )}
        </For>
      </div>
    </Show>
  );
}
