import DOMPurify from "dompurify";

// Defense-in-depth sanitize for source HTML before it reaches the shadow root. The Core mapper already escapes all
// Notion text/urls, but this is the same belt-and-suspenders innerHTML boundary the Markdown preview uses. The
// allowlist must permit the rich structure the mapper emits (details/summary, aside, figure, a disabled checkbox,
// table parts, the class attribute) or DOMPurify would strip it.
const CONFIG = {
  ALLOWED_TAGS: [
    "p",
    "br",
    "hr",
    "h1",
    "h2",
    "h3",
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
    "figure",
    "figcaption",
    "img",
    "input",
    "table",
    "tr",
    "td",
    "th",
  ],
  ALLOWED_ATTR: ["href", "src", "alt", "class", "type", "checked", "disabled"],
};

/** Sanitizes mapper-produced source HTML for injection into the SourceView shadow root. */
export function sanitizeSourceHtml(html: string): string {
  return DOMPurify.sanitize(html, CONFIG);
}
