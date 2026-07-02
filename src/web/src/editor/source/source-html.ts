import DOMPurify from "dompurify";

// Defense-in-depth sanitize for source HTML before it reaches the shadow root — the same belt-and-suspenders
// innerHTML boundary the Markdown preview uses, the last gate after `renderNotionMarkdown`. The allowlist must
// permit the structure that renderer emits (headings, details/summary, aside, table parts, img, the class
// attribute) — every Notion custom tag is already mapped to these standard tags before this runs.
const CONFIG = {
  ALLOWED_TAGS: [
    "p",
    "br",
    "hr",
    "h1",
    "h2",
    "h3",
    "h4",
    "h5",
    "h6",
    "strong",
    "em",
    "s",
    "u",
    "code",
    "pre",
    "span",
    "a",
    "ul",
    "ol",
    "li",
    "blockquote",
    "aside",
    "div",
    "details",
    "summary",
    "img",
    "table",
    "thead",
    "tbody",
    "tr",
    "td",
    "th",
  ],
  // data-wv-line is the renderer's block → original-markdown-line stamp; the edit path resolves clicks through it.
  ALLOWED_ATTR: ["href", "src", "alt", "class", "data-wv-line"],
};

/** Sanitizes mapper-produced source HTML for injection into the SourceView shadow root. */
export function sanitizeSourceHtml(html: string): string {
  return DOMPurify.sanitize(html, CONFIG);
}
