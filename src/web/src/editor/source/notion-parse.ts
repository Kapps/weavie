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
const TABLE_OPEN = /^<table[\s>]/;
// A code fence's open/close run: ``` or ~~~ (or longer, so a ````-fence can carry ``` inside).
const FENCE = /^(```+|~~~+)/;

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
    const fence = FENCE.exec(trimmed)?.[1] ?? null;
    if (fence !== null) {
      const close = regionClose(lines, i);
      out.push({
        kind: "fence",
        line: line.idx,
        lang: trimmed.slice(fence.length).trim(),
        code: lines
          .slice(i + 1, close)
          .map((l) => stripTabs(l.text, depth))
          .join("\n"),
      });
      i = Math.min(close + 1, lines.length);
      continue;
    }

    // A single-line block equation `$$…$$`; the multi-line `$$ … $$` form falls to the region reader below.
    if (trimmed.startsWith("$$") && trimmed.endsWith("$$") && trimmed.length > 4) {
      out.push({ kind: "equation", line: line.idx, tex: trimmed.slice(2, -2).trim() });
      i++;
      continue;
    }

    // A multi-line block equation `$$ … $$`.
    if (trimmed === "$$") {
      const close = regionClose(lines, i);
      out.push({
        kind: "equation",
        line: line.idx,
        tex: lines
          .slice(i + 1, close)
          .map((l) => stripTabs(l.text, depth))
          .join("\n"),
      });
      i = Math.min(close + 1, lines.length);
      continue;
    }

    // A `<table>` region (Notion's table form): consumed whole, parsed by notion-table.
    if (TABLE_OPEN.test(trimmed)) {
      const close = regionClose(lines, i);
      out.push(
        parseTagTable(
          lines.slice(i, close + 1).map((l) => l.text),
          line.idx,
        ),
      );
      i = Math.min(close + 1, lines.length);
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
    // Attrs parse from the OPENING TAG only — a caption containing `key="value"` text must not hijack them.
    const leaf = LEAF_TAG.exec(trimmed);
    if (leaf !== null) {
      const attrs = parseTagAttrs(leaf[2] ?? "");
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
      // A verbatim region (fence, equation, table) is delimited by its own syntax, not indentation — Notion even
      // emits a nested table's rows unindented — so skip it whole rather than let its interior dedent-break here.
      j = regionClose(lines, j) + 1;
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

// The line index closing the verbatim region opening at `start` — a fenced code block, a multi-line `$$` equation,
// or a `<table>` — `lines.length` when it never closes, or `start` when the line opens no such region.
function regionClose(lines: Line[], start: number): number {
  const trimmed = (lines[start]?.text ?? "").trim();
  const fence = FENCE.exec(trimmed)?.[1];
  if (fence !== undefined) {
    return scanFor(lines, start + 1, (t) => t.startsWith(fence));
  }
  if (trimmed === "$$") {
    return scanFor(lines, start + 1, (t) => t === "$$");
  }
  if (TABLE_OPEN.test(trimmed)) {
    return scanFor(lines, start, (t) => t.endsWith("</table>"));
  }
  return start;
}

// The first index at/after `from` whose trimmed line satisfies `hit`, or `lines.length` when none does.
function scanFor(lines: Line[], from: number, hit: (trimmed: string) => boolean): number {
  let j = from;
  while (j < lines.length && !hit((lines[j]?.text ?? "").trim())) {
    j++;
  }
  return j;
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
