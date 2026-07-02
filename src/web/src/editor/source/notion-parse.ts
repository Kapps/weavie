// Parses Notion's enhanced markdown — one block per line, tab-nested, with XML-like container tags — directly
// into the NotionBlock tree (docs/specs/notion-source-view.md). No CommonMark translation: the dialect's grammar
// is line-oriented, so each node natively carries the original line index the edit path anchors to. Pure strings,
// node-testable. Multi-line regions (fences, $$ equations, tables) are consumed verbatim, never re-parsed.

import { parseTagAttrs, parseTrailingAttrs } from "./notion-attrs";
import type { NotionBlock } from "./notion-blocks";
import { parsePipeTable, parseTagTable } from "./notion-table";

interface Line {
  text: string;
  idx: number;
}

/** Parses a page's enhanced markdown (verbatim, as fetched) into its block tree. */
export function parseNotion(markdown: string): NotionBlock[] {
  return parseBlocks(
    markdown.split("\n").map((text, idx) => ({ text, idx })),
    0,
  );
}

// The block-container tags: each holds tab-indented child blocks between its open and matching close tag.
const CONTAINERS = new Set([
  "callout",
  "columns",
  "column",
  "details",
  "synced_block",
  "synced_block_reference",
]);

const OPEN_TAG = /^<([a-z_]+)(?:\s[^>]*)?>$/;
const HEADING = /^(#{1,6})\s+(.*)$/;
const TODO = /^- \[([ xX])\]\s?(.*)$/;
const BULLET = /^[-*+]\s+(.*)$/;
const NUMBERED = /^(\d+)\.\s+(.*)$/;
const IMAGE = /^!\[(.*?)\]\((.*?)\)$/;
const LEAF_TAG = /^<(page|database|audio|video|file|pdf)\b([^>]*)>(.*)<\/\1>$/;
const UNKNOWN_TAG = /^<unknown\b([^>]*?)\/?>$/;
const TOC_TAG = /^<table_of_contents\b([^>]*?)\/?>$/;
const SUMMARY = /^<summary>(.*)<\/summary>$/;

// Parses sibling blocks at `depth` (the tab level of this slice); recursion carries container/child nesting.
function parseBlocks(lines: Line[], depth: number): NotionBlock[] {
  const out: NotionBlock[] = [];
  let i = 0;
  while (i < lines.length) {
    const line = lines[i] ?? { text: "", idx: -1 };
    const trimmed = line.text.trim();
    if (trimmed === "" || trimmed === "<empty-block/>") {
      i++; // Notion strips plain blank lines; <empty-block/> is v1-invisible (parity with the old renderer)
      continue;
    }

    // A fenced code block: its lines are code, consumed to the closing fence verbatim.
    const fence = trimmed.startsWith("```") ? "```" : trimmed.startsWith("~~~") ? "~~~" : null;
    if (fence !== null) {
      let j = i + 1;
      while (j < lines.length && !(lines[j]?.text ?? "").trim().startsWith(fence)) {
        j++;
      }
      out.push({
        kind: "fence",
        line: line.idx,
        lang: trimmed.slice(fence.length).trim(),
        code: lines
          .slice(i + 1, j)
          .map((l) => stripTabs(l.text, depth))
          .join("\n"),
      });
      i = Math.min(j + 1, lines.length);
      continue;
    }

    // A block equation: `$$ … $$` on its own lines (or a single `$$…$$` line).
    if (
      trimmed === "$$" ||
      (trimmed.startsWith("$$") && trimmed.endsWith("$$") && trimmed.length > 4)
    ) {
      if (trimmed !== "$$") {
        out.push({ kind: "equation", line: line.idx, tex: trimmed.slice(2, -2).trim() });
        i++;
        continue;
      }
      let j = i + 1;
      while (j < lines.length && (lines[j]?.text ?? "").trim() !== "$$") {
        j++;
      }
      out.push({
        kind: "equation",
        line: line.idx,
        tex: lines
          .slice(i + 1, j)
          .map((l) => stripTabs(l.text, depth))
          .join("\n"),
      });
      i = Math.min(j + 1, lines.length);
      continue;
    }

    // A `<table>` region (Notion's table form): consumed whole, parsed by notion-table.
    if (/^<table[\s>]/.test(trimmed)) {
      let j = i;
      while (j < lines.length && !(lines[j]?.text ?? "").trim().endsWith("</table>")) {
        j++;
      }
      j = Math.min(j, lines.length - 1);
      out.push(
        parseTagTable(
          lines.slice(i, j + 1).map((l) => l.text),
          line.idx,
        ),
      );
      i = j + 1;
      continue;
    }

    // A GFM pipe table (the guide's own example uses one): consecutive `|`-led lines.
    if (trimmed.startsWith("|")) {
      let j = i;
      while (j < lines.length && (lines[j]?.text ?? "").trim().startsWith("|")) {
        j++;
      }
      out.push(
        parsePipeTable(
          lines.slice(i, j).map((l) => l.text),
          line.idx,
        ),
      );
      i = j;
      continue;
    }

    // A container tag: recurse into its body (physically one tab deeper) at depth + 1.
    const tag = OPEN_TAG.exec(trimmed)?.[1];
    if (tag !== undefined && CONTAINERS.has(tag)) {
      const close = findClose(lines, i, tag);
      let inner = lines.slice(i + 1, close);
      const attrs = parseTagAttrs(trimmed);
      if (tag === "details") {
        const summary = SUMMARY.exec((inner[0]?.text ?? "").trim());
        if (summary !== null) {
          inner = inner.slice(1);
        }
        out.push({
          kind: "toggle",
          line: line.idx,
          color: attrs.color ?? null,
          summary: summary?.[1] ?? "",
          children: parseBlocks(inner, depth + 1),
        });
      } else if (tag === "callout") {
        out.push({
          kind: "callout",
          line: line.idx,
          color: attrs.color ?? null,
          icon: attrs.icon ?? "",
          children: parseBlocks(inner, depth + 1),
        });
      } else {
        out.push({
          kind: tag === "columns" ? "columns" : tag === "column" ? "column" : "synced",
          line: line.idx,
          children: parseBlocks(inner, depth + 1),
        });
      }
      i = close + 1;
      continue;
    }

    // Single-line leaf tags: page/database refs, non-image media, <unknown/> placeholders, the ToC marker.
    const leaf = LEAF_TAG.exec(trimmed);
    if (leaf !== null) {
      const attrs = parseTagAttrs(trimmed);
      out.push({
        kind: "card",
        line: line.idx,
        tag: leaf[1] as "page" | "database" | "audio" | "video" | "file" | "pdf",
        url: attrs.url ?? attrs.src ?? "",
        icon: attrs.icon ?? "",
        text: leaf[3] ?? "",
      });
      i++;
      continue;
    }
    if (UNKNOWN_TAG.test(trimmed)) {
      const attrs = parseTagAttrs(trimmed);
      out.push({
        kind: "card",
        line: line.idx,
        tag: "unknown",
        url: attrs.url ?? "",
        icon: "",
        // An <unknown> has no body; its `alt` names the block type it stands in for (e.g. "embed").
        text: (attrs.alt ?? "").replace(/_/g, " ").trim(),
      });
      i++;
      continue;
    }
    if (TOC_TAG.test(trimmed)) {
      out.push({ kind: "toc", line: line.idx, color: parseTagAttrs(trimmed).color ?? null });
      i++;
      continue;
    }

    // A one-line text block. The uniform child rule: following lines indented deeper belong to it —
    // this holds for EVERY block kind (paragraphs, todos, quotes, toggle headings, list items alike).
    let j = i + 1;
    while (j < lines.length) {
      const next = lines[j] ?? { text: "", idx: -1 };
      if (next.text.trim() !== "" && indentDepth(next.text) <= depth) {
        break;
      }
      j++;
    }
    const children = parseBlocks(lines.slice(i + 1, j), depth + 1);
    out.push(textBlock(trimmed, line.idx, children));
    i = j;
  }
  return out;
}

// Classifies a one-line block by its marker; trailing `{color=… toggle=…}` attrs are split off first.
function textBlock(trimmed: string, idx: number, children: NotionBlock[]): NotionBlock {
  const { rest, color, toggle } = parseTrailingAttrs(trimmed);
  const heading = HEADING.exec(rest);
  if (heading !== null) {
    return {
      kind: "heading",
      line: idx,
      level: (heading[1]?.length ?? 1) as 1 | 2 | 3 | 4 | 5 | 6,
      color,
      toggle,
      text: heading[2] ?? "",
      children,
    };
  }
  const todo = TODO.exec(rest);
  if (todo !== null) {
    return {
      kind: "todo",
      line: idx,
      color,
      checked: todo[1] !== " ",
      text: todo[2] ?? "",
      children,
    };
  }
  const numbered = NUMBERED.exec(rest);
  if (numbered !== null) {
    return {
      kind: "numbered",
      line: idx,
      color,
      number: Number(numbered[1]),
      text: numbered[2] ?? "",
      children,
    };
  }
  const bullet = BULLET.exec(rest);
  if (bullet !== null) {
    return { kind: "bulleted", line: idx, color, text: bullet[1] ?? "", children };
  }
  if (rest.startsWith(">")) {
    return { kind: "quote", line: idx, color, text: rest.replace(/^>\s?/, ""), children };
  }
  if (rest === "---") {
    return { kind: "divider", line: idx };
  }
  const image = IMAGE.exec(rest);
  if (image !== null) {
    return { kind: "image", line: idx, color, url: image[2] ?? "", caption: image[1] ?? "" };
  }
  return { kind: "paragraph", line: idx, color, text: rest, children };
}

// Leading tab count — the block's nesting depth.
function indentDepth(text: string): number {
  return /^\t*/.exec(text)?.[0].length ?? 0;
}

// Strips up to `depth` leading tabs (region content keeps its own deeper indentation, e.g. tab-indented code).
function stripTabs(text: string, depth: number): string {
  let n = 0;
  while (n < depth && text[n] === "\t") {
    n++;
  }
  return text.slice(n);
}

// The index of the `</tag>` closing the open tag at `openIdx` (nesting-aware); the last line when unclosed —
// input tolerance for remote content: the page still renders, and fetch-loss already surfaces via the banner.
function findClose(lines: Line[], openIdx: number, tag: string): number {
  let depth = 1;
  for (let i = openIdx + 1; i < lines.length; i++) {
    const t = (lines[i]?.text ?? "").trim();
    if (OPEN_TAG.exec(t)?.[1] === tag) {
      depth++;
    } else if (t === `</${tag}>`) {
      depth--;
      if (depth === 0) {
        return i;
      }
    }
  }
  return lines.length;
}
