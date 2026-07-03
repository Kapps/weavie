// Pure ops for the click-to-edit write path: slice a block's original line into byte-exact parts, and build the
// exact-match `{old_str, new_str}` op Notion's `PATCH /pages/{id}/markdown` (update_content) applies. Everything
// here works on the VERBATIM fetched markdown — never the normalized render text — so untouched formatting can't
// be rewritten. See docs/specs/notion-writes.md.

import { parseTrailingAttrs } from "./notion-attrs";

/**
 * A block line sliced for the inline editor: `line === tabs + display + attrsRaw`, byte-exact. The editor shows
 * only `display`; the leading tabs (nesting depth) and raw trailing `{…}` attrs (block color/toggle) are hidden
 * and re-attached verbatim on commit, so formatting survives an edit invisibly.
 */
export interface BlockSource {
  tabs: string;
  display: string;
  attrsRaw: string;
}

/** Slices original line `line` of `markdown` (0-based, from the renderer's `data-wv-line` stamp). */
export function blockSource(markdown: string, line: number): BlockSource {
  const text = markdown.split("\n")[line];
  if (text === undefined) {
    throw new Error(`No line ${line} in the source markdown.`);
  }
  const tabs = /^\t*/.exec(text)?.[0] ?? "";
  const rest = text.slice(tabs.length);
  // parseTrailingAttrs' rest is a prefix of its input, so the raw attr text is exactly what follows it.
  const display = parseTrailingAttrs(rest).rest;
  return { tabs, display, attrsRaw: rest.slice(display.length) };
}

/** The exact-match op to send, or the reason the draft can't become one (shown inline at the block). */
export type UpdateOp = { ok: true; oldStr: string; newStr: string } | { ok: false; reason: string };

/**
 * Builds the `update_content` op replacing line `line` of `markdown` with `newDisplay` (the edited display text —
 * tabs and trailing attrs are re-attached from the original). `oldStr` is the verbatim line, newline-anchored and
 * grown with whole neighbor lines until it matches exactly once in the document, because Notion applies the op as
 * a must-match-once search-and-replace; `newStr` is the identical context with only the target line replaced.
 */
export function buildUpdateOp(markdown: string, line: number, newDisplay: string): UpdateOp {
  if (newDisplay.includes("\n")) {
    return { ok: false, reason: "One block at a time — remove the line breaks." };
  }
  if (newDisplay.trim().length === 0) {
    return { ok: false, reason: "A block can't be emptied here — delete it in Notion instead." };
  }
  const lines = markdown.split("\n");
  const source = blockSource(markdown, line);
  const context = (start: number, end: number, lineText: string): string => {
    const range = lines.slice(start, end + 1);
    range[line - start] = lineText;
    // Boundary newlines anchor the match to whole lines (absent at the document's edges, where there is none).
    return (start > 0 ? "\n" : "") + range.join("\n") + (end < lines.length - 1 ? "\n" : "");
  };

  let start = line;
  let end = line;
  let growUp = true;
  while (countOccurrences(markdown, context(start, end, lines[line] ?? "")) > 1) {
    const canUp = start > 0;
    const canDown = end < lines.length - 1;
    if (!canUp && !canDown) {
      break; // the whole document — that always matches exactly once
    }
    // Alternate directions so the context grows around the block; take whichever side still has lines.
    if ((growUp && canUp) || !canDown) {
      start--;
    } else {
      end++;
    }
    growUp = !growUp;
  }

  return {
    ok: true,
    oldStr: context(start, end, lines[line] ?? ""),
    newStr: context(start, end, source.tabs + newDisplay + source.attrsRaw),
  };
}

function countOccurrences(haystack: string, needle: string): number {
  let count = 0;
  let index = haystack.indexOf(needle);
  while (index !== -1) {
    count++;
    index = haystack.indexOf(needle, index + 1);
  }
  return count;
}
