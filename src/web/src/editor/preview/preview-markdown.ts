import DOMPurify from "dompurify";
import MarkdownIt from "markdown-it";

// markdown-it renders to an HTML string; DOMPurify sanitizes it before it ever reaches the DOM. This is the
// app's one deliberate innerHTML — everywhere else builds nodes via textContent — so the sanitize is the
// boundary that keeps untrusted file content from executing.
const md = new MarkdownIt({ html: true, linkify: true, typographer: true });

/// Renders Markdown `content` into a detached, sanitized element for the Preview pane to mount.
export function renderMarkdown(content: string): HTMLElement {
  const el = document.createElement("div");
  el.innerHTML = DOMPurify.sanitize(md.render(content));
  return el;
}
