// The shell asks about the focused block editor without pulling the Markdown renderer onto the first-paint path.
import type { BlockEditor } from "./block-editor";

const editors = new WeakMap<Element, BlockEditor>();

export function registerBlockEditor(pane: Element, editor: BlockEditor): void {
  editors.set(pane, editor);
}

/** The block editor holding keyboard focus, for commands to target. */
export function focusedBlockEditor(): BlockEditor | undefined {
  const pane = document.activeElement?.closest(".editor-preview");
  return pane == null ? undefined : editors.get(pane);
}
