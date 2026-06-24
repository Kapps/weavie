import { Fzf } from "fzf";
import { canonicalFsPath } from "../editor/fs-path";

/// A workspace file split for display and fuzzy ranking. `leafStart` is the offset of `leaf` within `rel`,
/// mapping match positions (indices into `rel`) onto the leaf/dir spans.
export interface FileRow {
  abs: string;
  rel: string;
  leaf: string;
  dir: string;
  leafStart: number;
}

/// A ranked file match carrying the fuzzy-match positions (indices into `rel`) for highlighting.
export interface ScoredFile {
  row: FileRow;
  positions?: Set<number>;
}

// One file plus its lowercased `rel`, precomputed once so the per-keystroke pre-filter scans raw char codes
// without re-lowercasing 100k strings on every query.
interface Entry {
  row: FileRow;
  lower: string;
}

/// A prebuilt index over a fixed file set; rebuild it (via `createFileFinder`) only when the index changes,
/// then query it repeatedly with `rankFiles`.
export interface FileFinder {
  entries: readonly Entry[];
}

export function createFileFinder(rows: readonly FileRow[]): FileFinder {
  return { entries: rows.map((row) => ({ row, lower: row.rel.toLowerCase() })) };
}

// Above this many pre-filter survivors, only the best `RESCORE_CAP` are precision-scored. A query that narrows
// to at or under the cap is scored exactly as if the whole index were scored; only ultra-broad queries (which
// match more files than anyone scrolls and that you narrow by typing more) fall back to the heuristic cut.
const RESCORE_CAP = 2000;

// Cheap subsequence pre-filter + ranking hint (lower is better), the inner loop of the per-keystroke scan. Runs
// over every file, so it does no allocation: a two-pointer subsequence test, leaf first so it correlates with
// fzf's basename preference (a file search means the filename), then the whole path. Returns -1 for no match.
function preFilter(hay: string, needle: string, leafStart: number): number {
  const hl = hay.length;
  const nl = needle.length;
  let h = leafStart;
  let n = 0;
  let first = -1;
  let last = -1;
  while (h < hl && n < nl) {
    if (hay.charCodeAt(h) === needle.charCodeAt(n)) {
      if (n === 0) {
        first = h;
      }
      last = h;
      n++;
    }
    h++;
  }
  // Matched within the leaf: rank by how far into the filename it starts, then span (contiguity), then length.
  if (n === nl) {
    return (first - leafStart) * 1e4 + (last - first) * 1e2 + hl / 1000;
  }
  h = 0;
  n = 0;
  first = -1;
  last = -1;
  while (h < hl && n < nl) {
    if (hay.charCodeAt(h) === needle.charCodeAt(n)) {
      if (n === 0) {
        first = h;
      }
      last = h;
      n++;
    }
    h++;
  }
  // Matched only across the directory + name: always ranked below any leaf match (the 1e9 base).
  if (n === nl) {
    return 1e9 + first * 1e2 + (last - first) + hl / 1000;
  }
  return -1;
}

// How far into the filename the match begins: 0 means the filename itself starts with the query. A match that
// begins in the directory portion sorts last — for a file search the basename is what you mean. fzf scores a
// boundary hit the same whether it's "Sess" in SessionSlot or the "sess" after the hyphen in editor-session,
// so without this tiebreak those tie and fall back to raw path length, burying the files you actually meant.
function leafOffset(item: FileRow, positions: Set<number>): number {
  const first = Math.min(...positions);
  return first < item.leafStart ? Number.MAX_SAFE_INTEGER : first - item.leafStart;
}

/// Fuzzy-ranks the finder's files against `query` (best-first, uncapped). `recent` is most-recent-first
/// absolute paths. Match quality stays primary; ties then break by where the match lands (filename-start beats
/// mid-name beats directory-only), then recency, then path length — so the files you meant, and the ones you're
/// working in, surface first.
//
// Two phases keep this O(survivors) per keystroke instead of fzf's O(index): a cheap subsequence scan
// (`preFilter`) reduces the index to the matching files, then fzf's precision scorer (and its highlight
// positions) runs over at most `RESCORE_CAP` of them rather than the entire workspace.
export function rankFiles(
  finder: FileFinder,
  query: string,
  recent: readonly string[],
): ScoredFile[] {
  const needle = query.toLowerCase();
  const matched: { row: FileRow; pre: number }[] = [];
  for (const entry of finder.entries) {
    const pre = preFilter(entry.lower, needle, entry.row.leafStart);
    if (pre >= 0) {
      matched.push({ row: entry.row, pre });
    }
  }
  const candidates =
    matched.length > RESCORE_CAP
      ? matched
          .sort((a, b) => a.pre - b.pre)
          .slice(0, RESCORE_CAP)
          .map((m) => m.row)
      : matched.map((m) => m.row);

  const fzf = new Fzf(candidates, { selector: (r) => r.rel, casing: "case-insensitive" });
  const rank = new Map(recent.map((abs, i) => [canonicalFsPath(abs), i] as const));
  // Compute each tiebreak key once per match, then sort — not inside the comparator, which would recompute
  // leafOffset and canonicalFsPath O(n log n) times.
  return fzf
    .find(query)
    .map((r) => ({
      row: r.item,
      positions: r.positions,
      score: r.score,
      leaf: leafOffset(r.item, r.positions),
      recency: rank.get(canonicalFsPath(r.item.abs)) ?? Number.MAX_SAFE_INTEGER,
    }))
    .sort(
      (a, b) =>
        b.score - a.score ||
        a.leaf - b.leaf ||
        a.recency - b.recency ||
        a.row.rel.length - b.row.rel.length,
    )
    .map(({ row, positions }) => ({ row, positions }));
}
