import DOMPurify from "dompurify";
import MarkdownIt from "markdown-it";
import { highlightFence } from "./highlight";

// markdown-it renders to an HTML string; DOMPurify sanitizes it before it ever reaches the DOM. This is the
// app's one deliberate innerHTML — everywhere else builds nodes via textContent — so the sanitize is the
// boundary that keeps untrusted file content from executing.
const md = new MarkdownIt({ html: true, linkify: true, typographer: true });

// A `mermaid` fence becomes an inert placeholder carrying its escaped source; PreviewPane's async pass
// (hydrateMermaid) renders it after mount. Every other language is highlighted synchronously here. Both
// return a full `<pre …>` so markdown-it emits them verbatim; the sanitize in renderMarkdown keeps the
// boundary intact. Set after construction so the closure can reference md.utils without a type cycle.
md.set({
  highlight: (code, lang) =>
    lang === "mermaid"
      ? `<pre class="mermaid-pending">${md.utils.escapeHtml(code)}</pre>`
      : highlightFence(code, lang),
});

/// Renders Markdown `content` into a detached, sanitized element for the Preview pane to mount.
export function renderMarkdown(content: string): HTMLElement {
  const el = document.createElement("div");
  el.innerHTML = DOMPurify.sanitize(md.render(content));
  return el;
}
