import MarkdownIt from "markdown-it";
import {
  installLineStamps,
  normalizeNotionMarkdown,
  normalizeSelfClosing,
  notionColorClass,
  parseTrailingAttrs,
} from "./notion-transform";

// Renders Notion's enhanced markdown (the GET /pages/{id}/markdown body) to display HTML for the SourceView shadow
// root. markdown-it (html:true) parses the standard markdown and passes Notion's HTML-extension tags through; a
// DOM walk then maps those tags + color attributes onto the `.wv-*` classes source-styles.ts already defines. The
// caller (SourceView) sanitizes the result — DOMPurify is the last boundary, so the walk emits only standard tags.
const md = new MarkdownIt({ html: true, linkify: true });
// Mermaid fences become the inert placeholder SourceView's hydrateMermaid renders; any other fence falls back to
// markdown-it's default `<pre><code class="language-…">`, which SourceView's highlightCode then highlights.
md.set({
  highlight: (code, lang) =>
    lang === "mermaid" ? `<pre class="mermaid-pending">${md.utils.escapeHtml(code)}</pre>` : "",
});
// Blocks carry data-wv-line — the original markdown line that produced them — so a click can resolve back to the
// verbatim fetched line for the edit path.
installLineStamps(md);

/** Renders Notion enhanced-markdown to display HTML for the SourceView shadow root (the caller sanitizes it). */
export function renderNotionMarkdown(markdown: string): string {
  // normalizeNotionMarkdown turns Notion's one-block-per-line, tab-nested format into blank-line-separated
  // CommonMark first; without it markdown-it swallows whole regions as raw HTML blocks (see notion-transform).
  const normalized = normalizeNotionMarkdown(markdown);
  const doc = new DOMParser().parseFromString(
    normalizeSelfClosing(md.render(normalized.text, { lineMap: normalized.lineMap })),
    "text/html",
  );
  rewriteInlineSpans(doc.body);
  rewriteCustomTags(doc.body);
  applyBlockColors(doc.body);
  markEditableBlocks(doc.body);
  return doc.body.innerHTML;
}

// Marks the blocks the click-to-edit path can open: leaf text blocks that carry an original-line stamp. An element
// whose ancestor carries the same line loses its stamp (the blockquote owns its inner <p> — one block, one line);
// tables and code fences are v1 read-only, so their blocks keep the stamp but never the affordance.
function markEditableBlocks(root: HTMLElement): void {
  for (const el of [...root.querySelectorAll<HTMLElement>("[data-wv-line]")]) {
    const line = el.getAttribute("data-wv-line");
    if (el.parentElement?.closest(`[data-wv-line="${line}"]`) != null) {
      el.removeAttribute("data-wv-line");
      continue;
    }
    if (
      el.matches("p, h1, h2, h3, h4, h5, h6, li, blockquote") &&
      el.closest("table, pre") === null
    ) {
      el.classList.add("wv-editable");
    }
  }
}

// `<span color="blue">` / `<span underline="true">` → our color/underline classes (the raw attributes wouldn't style).
function rewriteInlineSpans(root: HTMLElement): void {
  for (const span of root.querySelectorAll<HTMLElement>("span[color], span[underline]")) {
    const color = span.getAttribute("color");
    if (color !== null) {
      addClass(span, notionColorClass(color));
      span.removeAttribute("color");
    }
    if (span.getAttribute("underline") === "true") {
      span.classList.add("wv-underline");
      span.removeAttribute("underline");
    }
  }
}

// Map every Notion HTML-extension tag onto semantic HTML + `.wv-*` classes. One pass over a static snapshot in
// document order (parents before children), so a replaced container's moved children are still visited.
function rewriteCustomTags(root: HTMLElement): void {
  for (const el of [...root.querySelectorAll<HTMLElement>("*")]) {
    const tag = el.tagName.toLowerCase();
    if (tag === "callout") {
      replaceCallout(el);
    } else if (tag === "columns") {
      rename(el, "div", "wv-columns");
    } else if (tag === "column") {
      rename(el, "div", "wv-column");
    } else if (
      tag === "page" ||
      tag === "database" ||
      tag === "file" ||
      tag === "pdf" ||
      tag === "audio" ||
      tag === "video" ||
      tag === "unknown"
    ) {
      toCard(el);
    } else if (tag.startsWith("mention-")) {
      toMention(el);
    } else if (tag === "table_of_contents" || tag === "empty-block") {
      el.remove();
    } else if (tag === "synced_block" || tag === "synced_block_reference") {
      el.replaceWith(...el.childNodes); // a transclusion wrapper — keep its content, drop the tag
    } else if (tag === "details") {
      applyColorAttr(el); // a real toggle; native <details> kept, just colored
    } else if (tag === "table") {
      fixTable(el);
    }
  }
}

// A block's trailing `{color="…" toggle="true"}` (Notion appends it to the block's first line; markdown-it leaves
// it as literal text). Strip it and apply the color class. A heading's `{toggle}` is dropped: the markdown API has
// no container for heading toggles, so v1 renders a plain heading (its content already follows as siblings).
function applyBlockColors(root: HTMLElement): void {
  for (const el of root.querySelectorAll<HTMLElement>(
    "p, h1, h2, h3, h4, h5, h6, li, blockquote",
  )) {
    const last = el.lastChild;
    if (last === null || last.nodeType !== 3 /* text */) {
      continue;
    }
    const { rest, color } = parseTrailingAttrs(last.textContent ?? "");
    if (color === null && rest === last.textContent) {
      continue;
    }
    last.textContent = rest;
    addClass(el, color === null ? null : notionColorClass(color));
  }
}

function replaceCallout(el: Element): void {
  const doc = el.ownerDocument;
  const aside = doc.createElement("aside");
  aside.className = join("wv-callout", notionColorClass(el.getAttribute("color") ?? ""));
  const icon = el.getAttribute("icon");
  if (icon !== null && icon.length > 0) {
    const span = doc.createElement("span");
    span.className = "wv-icon";
    span.textContent = icon;
    aside.append(span);
  }
  const body = doc.createElement("div");
  body.className = "wv-callout-body";
  body.append(...el.childNodes);
  aside.append(body);
  el.replaceWith(aside);
}

// Page/database refs, non-image media, and <unknown> placeholders (the markdown API's stand-in for embeds,
// bookmarks, link previews, …) render as a link "card" (never a live embed), mirroring the prior design.
function toCard(el: Element): void {
  const a = el.ownerDocument.createElement("a");
  a.className = "wv-card";
  const href = el.getAttribute("url") ?? el.getAttribute("src");
  if (href !== null) {
    a.setAttribute("href", href);
  }
  // An <unknown> has no body; its `alt` names the block type it stands in for (e.g. "embed", "link_preview").
  const alt = (el.getAttribute("alt") ?? "").replace(/_/g, " ").trim();
  const text = (el.textContent ?? "").trim() || alt;
  a.textContent = text.length > 0 ? text : (href ?? "");
  el.replaceWith(a);
}

function toMention(el: Element): void {
  const doc = el.ownerDocument;
  const url = el.getAttribute("url");
  const text = (el.textContent ?? "").trim() || (el.getAttribute("start") ?? "");
  if (url !== null && url.length > 0) {
    const a = doc.createElement("a");
    a.setAttribute("href", url);
    a.textContent = text;
    el.replaceWith(a);
  } else {
    el.replaceWith(doc.createTextNode(text));
  }
}

// Strip Notion's non-standard table attributes; honor header-row (first row → <th>) and per-cell/row color.
function fixTable(el: Element): void {
  const headerRow = el.getAttribute("header-row") === "true";
  for (const attr of ["fit-page-width", "header-row", "header-column"]) {
    el.removeAttribute(attr);
  }
  for (const colgroup of [...el.querySelectorAll("colgroup")]) {
    colgroup.remove(); // per-column color is unsupported in v1
  }
  [...el.querySelectorAll("tr")].forEach((tr, row) => {
    applyColorAttr(tr);
    for (const cell of [...tr.children]) {
      applyColorAttr(cell);
      if (headerRow && row === 0 && cell.tagName === "TD") {
        rename(cell, "th", cell.className);
      }
    }
  });
}

function rename(el: Element, tag: string, className: string): void {
  const repl = el.ownerDocument.createElement(tag);
  repl.className = className;
  repl.append(...el.childNodes);
  el.replaceWith(repl);
}

function applyColorAttr(el: Element): void {
  const color = el.getAttribute("color");
  if (color !== null) {
    addClass(el, notionColorClass(color));
    el.removeAttribute("color");
  }
}

function addClass(el: Element, className: string | null): void {
  if (className !== null) {
    el.classList.add(className);
  }
}

function join(...names: (string | null)[]): string {
  return names.filter((n): n is string => n !== null && n.length > 0).join(" ");
}
