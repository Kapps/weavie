// Renders Notion's enhanced markdown (the GET /pages/{id}/markdown body) to display HTML for the SourceView
// shadow root: a direct parse of the dialect (notion-parse) rendered from its block tree (notion-render) —
// no CommonMark translation, so every block natively knows the original markdown line the edit path anchors
// to. The caller (SourceView) sanitizes the result — DOMPurify is the last boundary, and the renderer emits
// only standard tags.

import { parseNotion } from "./notion-parse";
import { renderBlocks } from "./notion-render";

/** Renders Notion enhanced-markdown to display HTML for the SourceView shadow root (the caller sanitizes it). */
export function renderNotionMarkdown(markdown: string): string {
  return renderBlocks(parseNotion(markdown));
}
