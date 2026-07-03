// The block tree notion-parse.ts produces and notion-render.ts consumes — one node per Notion block, each
// carrying `line` (the ORIGINAL 0-based markdown line that produced it; the edit path's anchor) and, where the
// format allows tab-indented child blocks, `children`. `text` is always a block's own inline source (marker and
// trailing `{…}` attrs stripped) — notion-inline.ts renders it.

/** A block whose own text line can carry tab-indented child blocks beneath it. */
export interface TextBlock {
  line: number;
  color: string | null;
  text: string;
  children: NotionBlock[];
}

/** One row of a table block; `color` comes from the row's `<tr color>`. */
export interface TableRow {
  color: string | null;
  cells: { color: string | null; text: string }[];
}

/** A parsed Notion block. */
export type NotionBlock =
  | ({ kind: "paragraph" } & TextBlock)
  | ({ kind: "heading"; level: 1 | 2 | 3 | 4 | 5 | 6; toggle: boolean } & TextBlock)
  | ({ kind: "bulleted" } & TextBlock)
  | ({ kind: "numbered"; number: number } & TextBlock)
  | ({ kind: "todo"; checked: boolean } & TextBlock)
  | ({ kind: "quote" } & TextBlock)
  | { kind: "toggle"; line: number; color: string | null; summary: string; children: NotionBlock[] }
  | { kind: "callout"; line: number; color: string | null; icon: string; children: NotionBlock[] }
  | { kind: "columns" | "column" | "synced"; line: number; children: NotionBlock[] }
  | { kind: "fence"; line: number; lang: string; code: string }
  | { kind: "equation"; line: number; tex: string }
  | {
      kind: "table";
      line: number;
      headerRow: boolean;
      headerColumn: boolean;
      colColors: (string | null)[];
      rows: TableRow[];
    }
  | { kind: "divider"; line: number }
  | { kind: "image"; line: number; color: string | null; url: string; caption: string }
  | {
      kind: "card";
      line: number;
      tag: "page" | "database" | "audio" | "video" | "file" | "pdf" | "unknown";
      url: string;
      icon: string;
      text: string;
    }
  | { kind: "toc"; line: number; color: string | null };
