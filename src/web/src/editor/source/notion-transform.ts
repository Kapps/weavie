// Pure string/attribute helpers for rendering Notion's enhanced markdown. Split out from notion-markdown.ts (which
// owns the DOMParser walk) so these unit-test in the node vitest env; the DOM walk needs a browser DOM (covered by e2e).

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
const SELF_CLOSING = /<(empty-block|table_of_contents|mention-[a-z-]+)([^>]*?)\/>/g;

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
 * Converts Notion's enhanced markdown into blank-line-separated CommonMark `markdown-it` can parse. Notion emits
 * ONE block per line, single-`\n` separated (never blank lines), with tabs for nesting and HTML container tags —
 * which CommonMark misreads (an HTML tag swallows following lines as a raw block; tab indent becomes a code block).
 * This isolates each block with blank lines, drops `<empty-block/>` spacers, recurses into containers (dedenting
 * their children), and keeps code fences / `<table>` / lists intact.
 */
export function normalizeNotionMarkdown(markdown: string): string {
  return normalizeBlocks(markdown.split("\n"), 0).join("\n\n");
}

const LIST_ITEM = /^([-*+]|\d+\.)\s/;

function normalizeBlocks(lines: string[], baseIndent: number): string[] {
  const blocks: string[] = [];
  let i = 0;
  while (i < lines.length) {
    const line = lines[i] ?? "";
    const trimmed = line.trim();
    if (trimmed === "" || trimmed === "<empty-block/>") {
      i++;
      continue;
    }

    // A fenced code block: keep its lines intact (never split or reparse the code).
    const fence = trimmed.startsWith("```") ? "```" : trimmed.startsWith("~~~") ? "~~~" : null;
    if (fence !== null) {
      const start = i++;
      while (i < lines.length && !(lines[i] ?? "").trim().startsWith(fence)) {
        i++;
      }
      i = Math.min(i + 1, lines.length); // include the closing fence
      blocks.push(lines.slice(start, i).map(dedent).join("\n"));
      continue;
    }

    // A raw HTML table: emit the whole <table>…</table> as one block so its structure survives.
    if (trimmed.startsWith("<table")) {
      const close = findClose(lines, i, "table");
      blocks.push(
        lines
          .slice(i, close + 1)
          .map(dedent)
          .join("\n"),
      );
      i = close + 1;
      continue;
    }

    // A Notion container: isolate it with blank lines and recursively normalize its indented children.
    const tag = OPEN_TAG.exec(trimmed)?.[1];
    if (tag !== undefined && CONTAINER_TAGS.has(tag)) {
      const close = findClose(lines, i, tag);
      let open = dedent(line);
      let innerLines = lines.slice(i + 1, close);
      // A toggle's <summary> must stay a direct child of <details>; attach it to the open tag (no blank line) or
      // markdown-it wraps it in a <p> and the native toggle breaks.
      if (tag === "details" && (innerLines[0] ?? "").trim().startsWith("<summary")) {
        open = `${open}\n${dedent(innerLines[0] ?? "")}`;
        innerLines = innerLines.slice(1);
      }
      const inner = normalizeBlocks(innerLines, baseIndent + 1);
      blocks.push([open, ...inner, `</${tag}>`].join("\n\n"));
      i = close + 1;
      continue;
    }

    // A toggle heading (`## X {toggle="true"}`): the markdown API gives no container for it, so its children follow
    // at a deeper indent. Gather them into a collapsible <details> whose <summary> is the heading.
    if (/^#{1,6}\s/.test(trimmed) && /\btoggle="true"/.test(trimmed)) {
      const headingIndent = indentDepth(line);
      let j = i + 1;
      while (j < lines.length) {
        const t = (lines[j] ?? "").trim();
        if (t !== "" && t !== "<empty-block/>" && indentDepth(lines[j] ?? "") <= headingIndent) {
          break;
        }
        j++;
      }
      const children = normalizeBlocks(lines.slice(i + 1, j), baseIndent + 1);
      blocks.push(
        [
          '<details class="wv-toggle-heading">',
          "<summary>",
          dedent(line),
          "</summary>",
          ...children,
          "</details>",
        ].join("\n\n"),
      );
      i = j;
      continue;
    }

    // A list: keep consecutive items together (tight), re-indenting nested items as spaces for markdown-it.
    if (LIST_ITEM.test(trimmed)) {
      const start = i;
      while (i < lines.length && LIST_ITEM.test((lines[i] ?? "").trim())) {
        i++;
      }
      blocks.push(
        lines
          .slice(start, i)
          .map((item) => "  ".repeat(Math.max(0, indentDepth(item) - baseIndent)) + item.trim())
          .join("\n"),
      );
      continue;
    }

    blocks.push(dedent(line));
    i++;
  }
  return blocks;
}

function dedent(line: string): string {
  return line.replace(/^\t+/, "");
}

function indentDepth(line: string): number {
  return /^\t*/.exec(line)?.[0].length ?? 0;
}

// The index of the `</tag>` that closes the open tag at `openIdx`, accounting for nested same-name containers.
function findClose(lines: string[], openIdx: number, tag: string): number {
  let depth = 1;
  for (let i = openIdx + 1; i < lines.length; i++) {
    const t = (lines[i] ?? "").trim();
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
