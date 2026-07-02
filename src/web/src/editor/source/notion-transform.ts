// Pure string/attribute helpers for rendering Notion's enhanced markdown. Split out from notion-markdown.ts (which
// owns the DOMParser walk) so these unit-test in the node vitest env; the DOM walk needs a browser DOM (covered by e2e).

import type MarkdownIt from "markdown-it";

const COLORS = new Set([
  "gray",
  "brown",
  "orange",
  "yellow",
  "green",
  "blue",
  "purple",
  "pink",
  "red",
]);

/** Maps a Notion enhanced-markdown color ("blue" → text, "blue_bg" → background) to our stylesheet class, or null. */
export function notionColorClass(color: string): string | null {
  const c = color.trim().toLowerCase();
  const bg = c.endsWith("_bg");
  const base = bg ? c.slice(0, -3) : c;
  if (!COLORS.has(base)) {
    return null;
  }
  return bg ? `wv-bg-${base}` : `wv-color-${base}`;
}

/** A block's trailing `{key="value" …}` attribute (Notion appends one to a block's first line). */
export interface TrailingAttrs {
  /** The text with the trailing `{…}` removed. */
  rest: string;
  color: string | null;
  toggle: boolean;
}

const TRAILING = /\s*\{([^{}]*)\}\s*$/;

/** Splits a trailing `{color="…" toggle="true"}` off a block's text, returning the stripped text + parsed attrs. */
export function parseTrailingAttrs(text: string): TrailingAttrs {
  const match = TRAILING.exec(text);
  const body = match?.[1] ?? "";
  const color = /color="([^"]*)"/.exec(body)?.[1] ?? null;
  const toggle = /toggle="true"/.test(body);
  // Only a brace block that actually carries a Notion attribute is stripped — a literal trailing `{note}` is text.
  if (match === null || (color === null && !toggle)) {
    return { rest: text, color: null, toggle: false };
  }
  return { rest: text.slice(0, match.index), color, toggle };
}

// Notion's self-closing custom tags. The HTML parser ignores the "/" on an unknown element, so left as-is a
// `<empty-block/>` would swallow its following siblings as children — rewrite them to explicit empty pairs first.
const SELF_CLOSING = /<(empty-block|table_of_contents|unknown|mention-[a-z-]+)([^>]*?)\/>/g;

/** Rewrites Notion's self-closing custom tags (`<empty-block/>`, `<mention-date …/>`) to `<tag …></tag>` pairs. */
export function normalizeSelfClosing(html: string): string {
  return html.replace(SELF_CLOSING, "<$1$2></$1>");
}

// Notion's block-container tags: each holds tab-indented child blocks between its open and matching close tag.
const CONTAINER_TAGS = new Set([
  "callout",
  "columns",
  "column",
  "details",
  "synced_block",
  "synced_block_reference",
]);

const OPEN_TAG = /^<([a-z_]+)(?:\s[^>]*)?>$/;

/**
 * The normalizer's output: the blank-line-separated CommonMark `markdown-it` parses, plus a per-line map back to
 * the ORIGINAL markdown (normalized line index → original 0-based line index, -1 for synthesized lines). The map
 * is what lets the edit path resolve a rendered block to the verbatim fetched line it came from.
 */
export interface NormalizedDoc {
  text: string;
  lineMap: number[];
}

// One output line: its text plus the original line index it came from (-1 when the normalizer synthesized it).
interface Line {
  text: string;
  orig: number;
}

const BLANK: Line = { text: "", orig: -1 };

const synth = (text: string): Line => ({ text, orig: -1 });

/**
 * Converts Notion's enhanced markdown into blank-line-separated CommonMark `markdown-it` can parse. Notion emits
 * ONE block per line, single-`\n` separated (never blank lines), with tabs for nesting and HTML container tags —
 * which CommonMark misreads (an HTML tag swallows following lines as a raw block; tab indent becomes a code block).
 * This isolates each block with blank lines, drops `<empty-block/>` spacers, recurses into containers (dedenting
 * their children), and keeps code fences / `<table>` / lists intact. Every output line carries its original line
 * index in the returned `lineMap` (see `NormalizedDoc`).
 */
export function normalizeNotionMarkdown(markdown: string): NormalizedDoc {
  const out = normalizeBlocks(
    markdown.split("\n").map((text, orig) => ({ text, orig })),
    0,
  );
  return { text: out.map((l) => l.text).join("\n"), lineMap: out.map((l) => l.orig) };
}

// The block tokens worth stamping: the ones that render the editable block set (fences/tables stay v1 read-only).
const STAMP_TOKENS = new Set([
  "paragraph_open",
  "heading_open",
  "list_item_open",
  "blockquote_open",
]);

/**
 * Installs a core rule stamping each block-opening token with `data-wv-line` — the ORIGINAL markdown line index
 * that produced it, translated through the `NormalizedDoc.lineMap` the caller passes as render env
 * (`md.render(text, { lineMap })`). Synthesized lines (-1) stay unstamped, so their blocks render non-editable.
 */
export function installLineStamps(md: MarkdownIt): void {
  md.core.ruler.push("wv-line", (state) => {
    const lineMap = (state.env as { lineMap?: number[] }).lineMap;
    if (lineMap === undefined) {
      return;
    }
    for (const token of state.tokens) {
      const orig = token.map === null ? undefined : lineMap[token.map[0]];
      if (STAMP_TOKENS.has(token.type) && orig !== undefined && orig >= 0) {
        token.attrSet("data-wv-line", String(orig));
      }
    }
  });
}

const LIST_ITEM = /^([-*+]|\d+\.)\s/;

// Emits a flat line list with explicit BLANK separator lines between blocks (so text and lineMap stay in lockstep).
function normalizeBlocks(lines: Line[], baseIndent: number): Line[] {
  const out: Line[] = [];
  const push = (...block: Line[]): void => {
    if (out.length > 0) {
      out.push(BLANK);
    }
    out.push(...block);
  };
  let i = 0;
  while (i < lines.length) {
    const line = lines[i] ?? BLANK;
    const trimmed = line.text.trim();
    if (trimmed === "" || trimmed === "<empty-block/>") {
      i++;
      continue;
    }

    // A fenced code block: keep its lines intact (never split or reparse the code).
    const fence = trimmed.startsWith("```") ? "```" : trimmed.startsWith("~~~") ? "~~~" : null;
    if (fence !== null) {
      const start = i++;
      while (i < lines.length && !(lines[i]?.text ?? "").trim().startsWith(fence)) {
        i++;
      }
      i = Math.min(i + 1, lines.length); // include the closing fence
      push(...lines.slice(start, i).map(dedent));
      continue;
    }

    // A raw HTML table: emit the whole <table>…</table> as one block so its structure survives.
    if (trimmed.startsWith("<table")) {
      const close = findClose(lines, i, "table");
      push(...lines.slice(i, close + 1).map(dedent));
      i = close + 1;
      continue;
    }

    // A Notion container: isolate it with blank lines and recursively normalize its indented children.
    const tag = OPEN_TAG.exec(trimmed)?.[1];
    if (tag !== undefined && CONTAINER_TAGS.has(tag)) {
      const close = findClose(lines, i, tag);
      const open = [dedent(line)];
      let innerLines = lines.slice(i + 1, close);
      // A toggle's <summary> must stay a direct child of <details>; attach it to the open tag (no blank line) or
      // markdown-it wraps it in a <p> and the native toggle breaks.
      if (tag === "details" && (innerLines[0]?.text ?? "").trim().startsWith("<summary")) {
        open.push(dedent(innerLines[0] ?? BLANK));
        innerLines = innerLines.slice(1);
      }
      const inner = normalizeBlocks(innerLines, baseIndent + 1);
      const closing: Line = { text: `</${tag}>`, orig: lines[close]?.orig ?? -1 };
      push(...open, BLANK, ...(inner.length > 0 ? [...inner, BLANK] : []), closing);
      i = close + 1;
      continue;
    }

    // A toggle heading (`## X {toggle="true"}`): the markdown API gives no container for it, so its children follow
    // at a deeper indent. Gather them into a collapsible <details> whose <summary> is the heading.
    if (/^#{1,6}\s/.test(trimmed) && /\btoggle="true"/.test(trimmed)) {
      const headingIndent = indentDepth(line.text);
      let j = i + 1;
      while (j < lines.length) {
        const t = (lines[j]?.text ?? "").trim();
        if (
          t !== "" &&
          t !== "<empty-block/>" &&
          indentDepth(lines[j]?.text ?? "") <= headingIndent
        ) {
          break;
        }
        j++;
      }
      const children = normalizeBlocks(lines.slice(i + 1, j), baseIndent + 1);
      push(
        synth('<details class="wv-toggle-heading">'),
        BLANK,
        synth("<summary>"),
        BLANK,
        dedent(line),
        BLANK,
        synth("</summary>"),
        BLANK,
        ...(children.length > 0 ? [...children, BLANK] : []),
        synth("</details>"),
      );
      i = j;
      continue;
    }

    // A list: keep consecutive items together (tight), re-indenting nested items as spaces for markdown-it.
    if (LIST_ITEM.test(trimmed)) {
      const start = i;
      while (i < lines.length && LIST_ITEM.test((lines[i]?.text ?? "").trim())) {
        i++;
      }
      push(
        ...lines.slice(start, i).map((item) => ({
          text: "  ".repeat(Math.max(0, indentDepth(item.text) - baseIndent)) + item.text.trim(),
          orig: item.orig,
        })),
      );
      continue;
    }

    push(dedent(line));
    i++;
  }
  return out;
}

function dedent(line: Line): Line {
  return { text: line.text.replace(/^\t+/, ""), orig: line.orig };
}

function indentDepth(line: string): number {
  return /^\t*/.exec(line)?.[0].length ?? 0;
}

// The index of the `</tag>` that closes the open tag at `openIdx`, accounting for nested same-name containers.
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
  return lines.length; // unclosed — treat the rest as inner
}
