import type MarkdownIt from "markdown-it";
import type Token from "markdown-it/lib/token.mjs";

/** One top-level piece of a document: its 0-based source lines `[start, end)` and unsanitized HTML. */
export interface MarkdownBlock {
  start: number;
  end: number;
  html: string;
  /** Whether the source is structural (fence, table, HTML), so a new line never ends the block. */
  code: boolean;
}

// Per-load attribute names: a document's own raw HTML can't guess them to pose as a line stamp.
const stampId = crypto.randomUUID().slice(0, 8);
export const ITEM_START = `data-md-${stampId}-start`;
export const ITEM_END = `data-md-${stampId}-end`;

// Elements that never open a nesting level, by being void or having an optional end tag.
const UNPAIRED_TAGS = new Set(
  "area br col embed hr img input li link meta p td th tr wbr".split(" "),
);
const CODE_TOKENS = new Set(["fence", "code_block", "html_block", "table_open"]);

export const splitLines = (content: string): string[] => content.split(/\r?\n/);

/**
 * Renders `content` one top-level block at a time from a single parse, so reference links still resolve. List
 * items carry ITEM_START/ITEM_END offsets relative to their block, which keeps a block's HTML identical
 * when lines above it move. An HTML block left open (`<details>`) joins everything up to its closing tag.
 */
export function markdownBlocks(markdown: MarkdownIt, content: string): MarkdownBlock[] {
  const env = {};
  const lines = splitLines(content);
  return runs(markdown.parse(content, env)).map((run) => renderRun(markdown, run, lines, env));
}

/** The editable units of `content` — each list item's own lines, else each top-level block — parsed only. */
export function markdownUnits(markdown: MarkdownIt, content: string): LineRange[] {
  const lines = splitLines(content);
  return runs(markdown.parse(content, {})).flatMap((run) => {
    const items = run.flatMap((token, index) =>
      token.type === "list_item_open" && token.map !== null
        ? [{ start: token.map[0], end: ownEnd(run, index, lines) }]
        : [],
    );
    return items.length > 0 ? items : [runLines(run, lines)];
  });
}

/** 0-based source lines `[start, end)`. */
export interface LineRange {
  start: number;
  end: number;
}

// Top-level token runs; an HTML block left open joins everything up to its closing tag.
function runs(tokens: Token[]): Token[][] {
  const result: Token[][] = [];
  let run: Token[] = [];
  let depth = 0;
  let begin = 0;
  for (const [index, token] of tokens.entries()) {
    if (token.level !== 0 || token.nesting > 0) continue;
    const group = tokens.slice(begin, index + 1);
    begin = index + 1;
    run.push(...group);
    if (group[0]?.type === "html_block") depth = Math.max(0, depth + openTags(group[0].content));
    if (depth === 0) {
      result.push(run);
      run = [];
    }
  }
  if (run.length > 0) result.push(run);
  return result;
}

function runLines(run: Token[], lines: string[]): LineRange {
  const start = run[0]?.map?.[0] ?? 0;
  return {
    start,
    end: trimBlank(lines, start, Math.max(...run.map((token) => token.map?.[1] ?? 0))),
  };
}

function renderRun(
  markdown: MarkdownIt,
  run: Token[],
  lines: string[],
  env: object,
): MarkdownBlock {
  const { start, end } = runLines(run, lines);
  for (const [index, token] of run.entries()) {
    if (token.type !== "list_item_open" || token.map === null) continue;
    token.attrSet(ITEM_START, String(token.map[0] - start));
    token.attrSet(ITEM_END, String(ownEnd(run, index, lines) - start));
  }
  return {
    start,
    end,
    html: markdown.renderer.render(run, markdown.options, env),
    code: CODE_TOKENS.has(run[0]?.type ?? ""),
  };
}

// A list item's own lines stop at its nested list, so editing a parent leaves its children rendered.
function ownEnd(run: Token[], index: number, lines: string[]): number {
  const item = run[index] as Token;
  const [start, end] = item.map as [number, number];
  for (const token of run.slice(index + 1)) {
    if (token.type === "list_item_close" && token.level === item.level) break;
    if (token.level === item.level + 1 && /^(bullet|ordered)_list_open$/.test(token.type))
      return trimBlank(lines, start, token.map?.[0] ?? end);
  }
  return trimBlank(lines, start, end);
}

function trimBlank(lines: string[], start: number, end: number): number {
  let trimmed = end;
  while (trimmed > start + 1 && (lines[trimmed - 1] ?? "").trim() === "") trimmed -= 1;
  return trimmed;
}

// Comments match first, so a tag written inside one never counts.
const TAG = /<!--[\s\S]*?-->|<(\/?)([a-zA-Z][\w-]*)[^>]*?(\/?)>/g;

function openTags(html: string): number {
  let depth = 0;
  for (const [, close, name, selfClose] of html.matchAll(TAG)) {
    if (name !== undefined && selfClose === "" && !UNPAIRED_TAGS.has(name.toLowerCase()))
      depth += close === "" ? 1 : -1;
  }
  return depth;
}
