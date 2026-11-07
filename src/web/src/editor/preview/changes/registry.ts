// Which rendered previews review their file's changes, by model URI. Free of the renderer, so the editor shell
// can ask without loading the preview chunk.
import type * as monaco from "monaco-editor";
import type { DiffHunk, DiffMarkers } from "../../review/diff-markers";

/** A rendered preview reviewing its file's pending changes block by block. */
export interface PreviewReviewHandle {
  group(markers: DiffMarkers, original: string, model: monaco.editor.ITextModel): DiffHunk[];
  clear(): void;
  /** Shows the change at `line`; false when the file is reviewed in Source instead. */
  revealLine(line: number): boolean;
  focus(): void;
  confirmTarget(hunk: DiffHunk): boolean;
}

const reviews = new Map<string, PreviewReviewHandle>();
const listeners = new Set<() => void>();

export function registerPreviewReview(uri: string, review: PreviewReviewHandle): () => void {
  reviews.set(uri, review);
  for (const listener of listeners) listener();
  return () => {
    if (reviews.get(uri) !== review) return;
    reviews.delete(uri);
    for (const listener of listeners) listener();
  };
}

export function previewReviewFor(uri: string | undefined): PreviewReviewHandle | undefined {
  return uri === undefined ? undefined : reviews.get(uri);
}

/** Runs `listener` whenever a preview starts or stops reviewing; returns the unsubscribe. */
export function onPreviewReviewsChanged(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}
