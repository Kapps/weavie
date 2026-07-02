// Renders a block's inline enhanced-markdown text to HTML: standard marks (**bold**, *italic*, ~~strike~~,
// `code`, [link](url)), Notion's escapes (\ * ~ ` $ [ ] < > { } | ^), inline math, <br>, <span color|underline>,
// and <mention-*> tags. Everything unrecognized is HTML-escaped — only known constructs ever produce markup, so
// DOMPurify stays belt-and-suspenders, never load-bearing. Pure strings, node-testable.

import { notionColorClass, parseTagAttrs } from "./notion-attrs";

const ESCAPABLE = new Set(["\\", "*", "~", "`", "$", "[", "]", "<", ">", "{", "}", "|", "^"]);

/** Renders one block's inline source text to HTML (the caller sanitizes the assembled document). */
export function renderInline(text: string): string {
  let out = "";
  let i = 0;
  while (i < text.length) {
    const c = text[i] ?? "";
    if (c === "\\" && ESCAPABLE.has(text[i + 1] ?? "")) {
      out += escapeHtml(text[i + 1] ?? "");
      i += 2;
    } else if (c === "`") {
      const close = text.indexOf("`", i + 1);
      if (close > i + 1) {
        out += `<code>${escapeHtml(text.slice(i + 1, close))}</code>`;
        i = close + 1;
      } else {
        out += escapeHtml(c);
        i++;
      }
    } else if (c === "$") {
      const close = findDelim(text, i + 1, "$");
      if (close > i + 1) {
        out += `<code class="wv-math">${escapeHtml(text.slice(i + 1, close))}</code>`;
        i = close + 1;
      } else {
        out += escapeHtml(c);
        i++;
      }
    } else if (text.startsWith("**", i) || text.startsWith("~~", i)) {
      const delim = text.slice(i, i + 2);
      const close = findDelim(text, i + 2, delim);
      if (close > i + 2) {
        const tag = delim === "**" ? "strong" : "s";
        out += `<${tag}>${renderInline(text.slice(i + 2, close))}</${tag}>`;
        i = close + 2;
      } else {
        out += escapeHtml(delim);
        i += 2;
      }
    } else if (c === "*") {
      const close = findDelim(text, i + 1, "*");
      if (close > i + 1) {
        out += `<em>${renderInline(text.slice(i + 1, close))}</em>`;
        i = close + 1;
      } else {
        out += escapeHtml(c);
        i++;
      }
    } else if (c === "[") {
      // A citation `[^URL]` has no `](`, so it falls through and renders as literal text (v1 parity).
      const link = /^\[((?:\\.|[^\]\\])*)\]\(([^)]*)\)/.exec(text.slice(i));
      if (link !== null) {
        out += `<a href="${escapeHtml(link[2] ?? "")}">${renderInline(link[1] ?? "")}</a>`;
        i += link[0].length;
      } else {
        out += escapeHtml(c);
        i++;
      }
    } else if (c === "<") {
      const [html, consumed] = inlineTag(text.slice(i));
      out += html;
      i += consumed;
    } else {
      out += escapeHtml(c);
      i++;
    }
  }
  return out;
}

// Renders a recognized inline tag at the start of `rest`, or the escaped `<` alone. Returns [html, chars consumed].
function inlineTag(rest: string): [string, number] {
  const br = /^<br\s*\/?>/i.exec(rest);
  if (br !== null) {
    return ["<br>", br[0].length];
  }
  const span = /^<span\b[^>]*>/.exec(rest);
  if (span !== null) {
    const close = findTagClose(rest, span[0].length, "span");
    if (close !== null) {
      const attrs = parseTagAttrs(span[0]);
      const classes = [
        attrs.color === undefined ? null : notionColorClass(attrs.color),
        attrs.underline === "true" ? "wv-underline" : null,
      ].filter((n): n is string => n !== null);
      const inner = renderInline(rest.slice(span[0].length, close.start));
      return [
        classes.length > 0 ? `<span class="${classes.join(" ")}">${inner}</span>` : inner,
        close.end,
      ];
    }
  }
  const date = /^<mention-date\b([^>]*?)\/>/.exec(rest);
  if (date !== null) {
    const attrs = parseTagAttrs(date[0]);
    const start = [attrs.start ?? "", attrs.startTime ?? ""].filter((s) => s !== "").join(" ");
    const end = attrs.end ?? "";
    return [escapeHtml(end === "" ? start : `${start} → ${end}`), date[0].length];
  }
  const mention = /^<(mention-[a-z-]+)\b[^>]*>/.exec(rest);
  if (mention !== null) {
    const tag = mention[1] ?? "";
    const closeTag = `</${tag}>`;
    const close = rest.indexOf(closeTag, mention[0].length);
    if (close >= 0) {
      const url = parseTagAttrs(mention[0]).url ?? "";
      const inner = escapeHtml(rest.slice(mention[0].length, close));
      return [
        url === "" ? inner : `<a href="${escapeHtml(url)}">${inner}</a>`,
        close + closeTag.length,
      ];
    }
  }
  return [escapeHtml("<"), 1];
}

// The index of the next unescaped `delim` at or after `from` (skipping backslash escapes), or -1. A `**` close
// landing on a `***` run yields its first star to the inner emphasis (`**bold *inner***`, `***both***`).
function findDelim(text: string, from: number, delim: string): number {
  for (let i = from; i < text.length; i++) {
    if (text[i] === "\\") {
      i++;
    } else if (text.startsWith(delim, i)) {
      return delim === "**" && text.startsWith("***", i) && text[i + 3] !== "*" ? i + 1 : i;
    }
  }
  return -1;
}

// The matching `</tag>` for an open tag ending at `from`, nesting-aware; null when unclosed.
function findTagClose(
  text: string,
  from: number,
  tag: string,
): { start: number; end: number } | null {
  const open = new RegExp(`^<${tag}\\b[^>]*>`);
  let depth = 1;
  for (let i = from; i < text.length; i++) {
    if (text[i] !== "<") {
      continue;
    }
    if (text.startsWith(`</${tag}>`, i)) {
      depth--;
      if (depth === 0) {
        return { start: i, end: i + tag.length + 3 };
      }
    } else if (open.test(text.slice(i))) {
      depth++;
    }
  }
  return null;
}

/** HTML-escapes text for element content or a double-quoted attribute value. */
export function escapeHtml(text: string): string {
  return text
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}
