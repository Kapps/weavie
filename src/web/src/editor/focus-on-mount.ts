import { onMount } from "solid-js";

// Retains editor focus across overlay replacement without letting a session-driven mount claim another pane.
// Only claims the host itself: a shadow-tree descendant that already owns focus (e.g. a block editor textarea
// SourceView just focused while rendering) must keep it — stealing it back to the host is how a draft reopened
// on a session switch loses the keyboard the instant it's restored, even though it just received it.
export function preserveEditorFocusOnMount(
  host: () => HTMLElement,
  editorFocused: () => boolean,
): void {
  onMount(() => {
    if (editorFocused() && host().shadowRoot?.activeElement == null) {
      host().focus();
    }
  });
}
