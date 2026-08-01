import { onMount } from "solid-js";

// Retains editor focus across overlay replacement without letting a session-driven mount claim another pane.
export function preserveEditorFocusOnMount(
  host: () => HTMLElement,
  editorFocused: () => boolean,
): void {
  onMount(() => {
    if (editorFocused()) {
      host().focus();
    }
  });
}
