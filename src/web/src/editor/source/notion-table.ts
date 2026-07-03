// Both halves of the table concern: parsing Notion's `<table>` form AND the GFM pipe form (the API's own
// examples use both) into the one `table` block, and rendering that block to HTML. Tables are v1 read-only —
// no cell carries an edit stamp. Color precedence (cell over row over column) matches the format spec.

import { notionColorClass, parseTagAttrs } from "./notion-attrs";
import type { NotionBlock, TableRow } from "./notion-blocks";
import { renderInline } from "./notion-inline";

type Table = Extract<NotionBlock, { kind: "table" }>;

const COL = /<col\b([^>]*?)\/?>/g;
const ROW = /<tr\b([^>]*)>([\s\S]*?)<\/tr>/g;
const CELL = /<td\b([^>]*)>([\s\S]*?)<\/td>/g;

/** Parses a `<table …>…</table>` region (its lines verbatim) into a table block. */
export function parseTagTable(lines: string[], line: number): Table {
  const source = lines.join("\n");
  const attrs = parseTagAttrs(/^<table\b[^>]*>/.exec(source.trim())?.[0] ?? "");
  const colgroup = /<colgroup>([\s\S]*?)<\/colgroup>/.exec(source);
  const colColors = [...(colgroup?.[1] ?? "").matchAll(COL)].map(
    (m) => parseTagAttrs(m[1] ?? "").color ?? null,
  );
  const rows: TableRow[] = [...source.matchAll(ROW)].map((row) => ({
    color: parseTagAttrs(row[1] ?? "").color ?? null,
    cells: [...(row[2] ?? "").matchAll(CELL)].map((cell) => ({
      color: parseTagAttrs(cell[1] ?? "").color ?? null,
      text: (cell[2] ?? "").trim(),
    })),
  }));
  return {
    kind: "table",
    line,
    headerRow: attrs["header-row"] === "true",
    headerColumn: attrs["header-column"] === "true",
    colColors,
    rows,
  };
}

// An unescaped cell separator (`\|` is Notion's escaped literal pipe).
const PIPE_SPLIT = /(?<!\\)\|/;
const SEPARATOR = /^\|?[\s:|-]*-[\s:|-]*\|?$/;

/** Parses consecutive GFM pipe-table lines into a table block (a `|---|` second line marks a header row). */
export function parsePipeTable(lines: string[], line: number): Table {
  const headerRow = lines.length > 1 && SEPARATOR.test((lines[1] ?? "").trim());
  const rows: TableRow[] = lines
    .filter((_, n) => !(headerRow && n === 1))
    .map((text) => ({
      color: null,
      cells: text
        .trim()
        .replace(/^\|/, "")
        .replace(/\|$/, "")
        .split(PIPE_SPLIT)
        .map((cell) => ({ color: null, text: cell.trim() })),
    }));
  return { kind: "table", line, headerRow, headerColumn: false, colColors: [], rows };
}

/** Renders a table block to HTML (header flags → `<th>`, cell/row/column colors → `.wv-*` classes). */
export function renderTable(table: Table): string {
  const rows = table.rows.map((row, r) => {
    const cells = row.cells.map((cell, c) => {
      const header = (table.headerRow && r === 0) || (table.headerColumn && c === 0);
      const tag = header ? "th" : "td";
      const color = cell.color ?? table.colColors[c] ?? null;
      const cls = color === null ? null : notionColorClass(color);
      return `<${tag}${cls === null ? "" : ` class="${cls}"`}>${renderInline(cell.text)}</${tag}>`;
    });
    const cls = row.color === null ? null : notionColorClass(row.color);
    return `<tr${cls === null ? "" : ` class="${cls}"`}>${cells.join("")}</tr>`;
  });
  return `<table>${rows.join("")}</table>`;
}
