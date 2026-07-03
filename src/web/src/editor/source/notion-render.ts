// Renders the NotionBlock tree to display HTML for the SourceView shadow root (the caller sanitizes it). Emits
// only standard tags with the `.wv-*` classes source-styles.ts defines. Editable blocks — the leaf text kinds
// (p, h1–h6, li, blockquote) — carry `class="wv-editable" data-wv-line="N"`, the edit path's anchor; a block's
// tab-indented children render in a `.wv-children` beside its own inline content. Pure strings, node-testable.

import { notionColorClass } from "./notion-attrs";
import type { NotionBlock } from "./notion-blocks";
import { escapeHtml, renderInline } from "./notion-inline";
import { renderTable } from "./notion-table";

/** Renders a parsed page's block tree to HTML. */
export function renderBlocks(blocks: NotionBlock[]): string {
  return render(blocks, collectHeadings(blocks));
}

// A heading's ToC entry: its dedup'd anchor slug, level, and plain-text label.
interface Heading {
  slug: string;
  level: number;
  label: string;
}

function render(blocks: NotionBlock[], headings: Map<NotionBlock, Heading>): string {
  let out = "";
  let i = 0;
  while (i < blocks.length) {
    const block = blocks[i] as NotionBlock;
    // Consecutive list-kind siblings group into one list element.
    if (block.kind === "bulleted" || block.kind === "numbered" || block.kind === "todo") {
      const kind = block.kind;
      const run: NotionBlock[] = [];
      while (i < blocks.length && blocks[i]?.kind === kind) {
        run.push(blocks[i] as NotionBlock);
        i++;
      }
      const items = run.map((item) => listItem(item, headings)).join("");
      if (kind === "todo") {
        out += `<ul class="wv-todos">${items}</ul>`;
      } else if (kind === "bulleted") {
        out += `<ul>${items}</ul>`;
      } else {
        const start = block.kind === "numbered" ? block.number : 1;
        out += start === 1 ? `<ol>${items}</ol>` : `<ol start="${start}">${items}</ol>`;
      }
      continue;
    }
    out += renderBlock(block, headings);
    i++;
  }
  return out;
}

// The list kinds never reach here — render()'s grouping loop consumes them into <ul>/<ol> runs.
type NonListBlock = Exclude<NotionBlock, { kind: "bulleted" | "numbered" | "todo" }>;

function renderBlock(block: NonListBlock, headings: Map<NotionBlock, Heading>): string {
  switch (block.kind) {
    case "paragraph":
      return (
        element("p", block, renderInline(block.text), "", "") +
        childrenDiv(block.children, headings)
      );
    case "heading": {
      const slug = headings.get(block)?.slug ?? "";
      const h = element(`h${block.level}`, block, renderInline(block.text), ` id="${slug}"`, "");
      // A toggle heading collapses its children; SourceView drives the <details> open/close on summary click.
      return block.toggle
        ? `<details class="wv-toggle-heading"><summary>${h}</summary>${childrenDiv(block.children, headings)}</details>`
        : h + childrenDiv(block.children, headings);
    }
    case "quote":
      // Children render inside the blockquote so the quote bar spans them.
      return element(
        "blockquote",
        block,
        renderInline(block.text) + childrenDiv(block.children, headings),
        "",
        "",
      );
    case "toggle":
      return `<details${classAttr(colorClass(block.color))}><summary>${renderInline(block.summary)}</summary>${childrenDiv(block.children, headings)}</details>`;
    case "callout": {
      const icon =
        block.icon === "" ? "" : `<span class="wv-icon">${escapeHtml(block.icon)}</span>`;
      return `<aside${classAttr("wv-callout", colorClass(block.color))}>${icon}<div class="wv-callout-body">${render(block.children, headings)}</div></aside>`;
    }
    case "columns":
      return `<div class="wv-columns">${render(block.children, headings)}</div>`;
    case "column":
      return `<div class="wv-column">${render(block.children, headings)}</div>`;
    case "synced": // a transclusion wrapper — the content renders, the wrapper is invisible
      return render(block.children, headings);
    case "fence":
      // The shapes SourceView's highlightCode / mermaid hydration expect, matching the Markdown preview.
      if (block.lang === "mermaid") {
        return `<pre class="mermaid-pending">${escapeHtml(block.code)}</pre>`;
      }
      return block.lang === ""
        ? `<pre><code>${escapeHtml(block.code)}</code></pre>`
        : `<pre><code class="language-${escapeHtml(block.lang)}">${escapeHtml(block.code)}</code></pre>`;
    case "equation": // literal-but-styled TeX (KaTeX is a fast-follow)
      return `<div class="wv-equation">${escapeHtml(block.tex)}</div>`;
    case "table":
      return renderTable(block);
    case "divider":
      return "<hr>";
    case "image":
      return element(
        "p",
        block,
        `<img src="${escapeHtml(block.url)}" alt="${escapeHtml(block.caption)}">`,
        "",
        "",
      );
    case "card": {
      const label = block.text.trim() === "" ? block.url : block.text.trim();
      const icon = block.icon === "" ? "" : `${block.icon} `;
      return `<a class="wv-card" href="${escapeHtml(block.url)}">${escapeHtml(icon)}${renderInline(label)}</a>`;
    }
    case "toc": {
      const items = [...headings.values()]
        .map(
          (h) =>
            `<li class="wv-toc-l${h.level}"><a href="#${h.slug}">${escapeHtml(h.label)}</a></li>`,
        )
        .join("");
      return `<nav${classAttr("wv-toc", colorClass(block.color))}><ul>${items}</ul></nav>`;
    }
  }
}

// A list-kind block's <li>; children (nested lists, child paragraphs) stay inside the item.
function listItem(block: NotionBlock, headings: Map<NotionBlock, Heading>): string {
  if (block.kind === "todo") {
    const box = block.checked
      ? '<input type="checkbox" checked disabled>'
      : '<input type="checkbox" disabled>';
    return element(
      "li",
      block,
      `${box}<span class="wv-todo-text">${renderInline(block.text)}</span>${childrenDiv(block.children, headings)}`,
      "",
      "wv-todo",
    );
  }
  if (block.kind === "bulleted" || block.kind === "numbered") {
    return element(
      "li",
      block,
      renderInline(block.text) + childrenDiv(block.children, headings),
      "",
      "",
    );
  }
  return "";
}

// An editable text element: the color class, the wv-editable mark, and the data-wv-line edit anchor.
function element(
  tag: string,
  block: { line: number; color: string | null },
  inner: string,
  extraAttrs: string,
  extraClass: string,
): string {
  const cls = classAttr(extraClass, "wv-editable", colorClass(block.color));
  return `<${tag}${extraAttrs}${cls} data-wv-line="${block.line}">${inner}</${tag}>`;
}

function childrenDiv(children: NotionBlock[], headings: Map<NotionBlock, Heading>): string {
  return children.length === 0
    ? ""
    : `<div class="wv-children">${render(children, headings)}</div>`;
}

function colorClass(color: string | null): string | null {
  return color === null ? null : notionColorClass(color);
}

function classAttr(...names: (string | null)[]): string {
  const joined = names.filter((n): n is string => n !== null && n.length > 0).join(" ");
  return joined === "" ? "" : ` class="${joined}"`;
}

// Collects every heading in document order (containers included) with a dedup'd anchor slug — the ToC's source
// and the headings' `id`s.
function collectHeadings(blocks: NotionBlock[]): Map<NotionBlock, Heading> {
  const out = new Map<NotionBlock, Heading>();
  const used = new Map<string, number>();
  const walk = (list: NotionBlock[]): void => {
    for (const block of list) {
      if (block.kind === "heading") {
        const label = plainText(block.text);
        const base = `wv-h-${label
          .toLowerCase()
          .replace(/[^\p{L}\p{N}]+/gu, "-")
          .replace(/^-+|-+$/g, "")}`;
        const n = used.get(base) ?? 0;
        used.set(base, n + 1);
        out.set(block, { slug: n === 0 ? base : `${base}-${n + 1}`, level: block.level, label });
      }
      if ("children" in block) {
        walk(block.children);
      }
    }
  };
  walk(blocks);
  return out;
}

// A heading's plain-text label: escapes resolved, marks/tags/links reduced to their text.
function plainText(text: string): string {
  let out = text.replace(/\[((?:\\.|[^\]\\])*)\]\([^)]*\)/g, "$1");
  // Strip tags to a fixed point: one pass can splice a new tag together (`<scr<b>ipt>`), which would leak
  // tag-looking residue into the label (and reads as an injection risk to scanners, though escapeHtml and
  // DOMPurify both sit downstream).
  for (let prev = ""; prev !== out; ) {
    prev = out;
    out = out.replace(/<[^>]*>/g, "");
  }
  return out
    .replace(/(\*\*|~~|[*`$])/g, "")
    .replace(/\\(.)/g, "$1")
    .trim();
}
