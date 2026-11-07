// The mounted Markdown previews, found by focus or by the model they show. Type-only imports keep the renderer
// off the shell's first-paint path.
import type { BlockEditor } from "./block-editor";
import type { PreviewReview } from "./changes/preview-review";

export interface PreviewPaneHandle {
  host: Element;
  editor: BlockEditor;
  review: PreviewReview;
  /** The model URI the pane shows, once its working copy is loaded. */
  uri(): string | undefined;
}

const panes = new Set<PreviewPaneHandle>();
const listeners = new Set<() => void>();
const notify = (): void => {
  for (const listener of listeners) listener();
};

/** Mounts a pane; returns its unmount. */
export function registerPreviewPane(pane: PreviewPaneHandle): () => void {
  panes.add(pane);
  notify();
  return () => {
    panes.delete(pane);
    notify();
  };
}

/** Runs `listener` whenever a pane mounts or unmounts; returns the unsubscribe. */
export function onPreviewPanesChanged(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

/** The change review of the pane showing model `uri`. */
export function previewReviewFor(uri: string | undefined): PreviewReview | undefined {
  return uri === undefined ? undefined : [...panes].find((pane) => pane.uri() === uri)?.review;
}

function focusedPane(): PreviewPaneHandle | undefined {
  const host = document.activeElement?.closest(".editor-preview");
  return [...panes].find((pane) => pane.host === host);
}

/** The block editor holding keyboard focus, for commands to target. */
export const focusedBlockEditor = (): BlockEditor | undefined => focusedPane()?.editor;

/** The change review of the preview holding keyboard focus. */
export const focusedPreviewReview = (): PreviewReview | undefined => focusedPane()?.review;
