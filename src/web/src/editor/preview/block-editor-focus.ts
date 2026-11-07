// The shell asks about the focused block editor without pulling the Markdown renderer onto the first-paint path.
import type { BlockEditor } from "./block-editor";
import type { PreviewReview } from "./changes/preview-review";

const editors = new WeakMap<Element, BlockEditor>();
const reviews = new WeakMap<Element, PreviewReview>();

export function registerBlockEditor(pane: Element, editor: BlockEditor): void {
  editors.set(pane, editor);
}

export function registerPaneReview(pane: Element, review: PreviewReview): void {
  reviews.set(pane, review);
}

const focusedPane = (): Element | undefined =>
  document.activeElement?.closest(".editor-preview") ?? undefined;

/** The block editor holding keyboard focus, for commands to target. */
export function focusedBlockEditor(): BlockEditor | undefined {
  const pane = focusedPane();
  return pane === undefined ? undefined : editors.get(pane);
}

/** The change review of the preview holding keyboard focus. */
export function focusedPreviewReview(): PreviewReview | undefined {
  const pane = focusedPane();
  return pane === undefined ? undefined : reviews.get(pane);
}
