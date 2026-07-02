import DOMPurify from "dompurify";

// Defense-in-depth sanitize for source HTML before it reaches the shadow root — the same belt-and-suspenders
// innerHTML boundary the Markdown preview uses, the last gate after `renderNotionMarkdown`. The allowlist must
// permit the structure that renderer emits (headings, details/summary, aside, table parts, img, the class
// attribute) — every Notion custom tag is already mapped to these standard tags before this runs.
/** The tags source HTML may carry — the renderer's full output vocabulary (a corpus test pins the ⊆ relation). */
export const SOURCE_ALLOWED_TAGS: readonly string[] = [
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
  "input",
  "nav",
];

const CONFIG = {
  ALLOWED_TAGS: [...SOURCE_ALLOWED_TAGS],
  // data-wv-line is the renderer's block → original-markdown-line stamp; the edit path resolves clicks through
  // it. id anchors headings for the ToC (DOMPurify's SANITIZE_DOM keeps ids clobber-safe); type/checked/disabled
  // are the read-only to-do checkbox; start numbers a list resuming mid-sequence.
  ALLOWED_ATTR: [
    "href",
    "src",
    "alt",
    "class",
    "data-wv-line",
    "id",
    "type",
    "checked",
    "disabled",
    "start",
  ],
};

/** Sanitizes mapper-produced source HTML for injection into the SourceView shadow root. */
export function sanitizeSourceHtml(html: string): string {
  return DOMPurify.sanitize(html, CONFIG);
}
