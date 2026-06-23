import { Fzf, type FzfResultItem } from "fzf";
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

/// A prebuilt fuzzy finder over a fixed file index; rebuild it (via `createFileFinder`) only when the index
/// changes, then query it repeatedly with `rankFiles`.
export type FileFinder = Fzf<readonly FileRow[]>;

// Case-insensitive so "sess" and "Sess" rank a file identically — capitalization in the query never gates a
// match or skews order. Diacritics are folded (normalize, on by default). camelCase/segment boundaries still
// score, so "SES" ranks SimpleEmailService and "FSR" ranks FileStreamReader.
export function createFileFinder(rows: readonly FileRow[]): FileFinder {
  return new Fzf(rows, { selector: (r) => r.rel, casing: "case-insensitive" });
}

// Shortest query that earns the single-deletion typo retry — shorter queries already match almost everything,
// so a typo fallback there only adds noise.
const TYPO_MIN_LEN = 4;

// fzf is subsequence-only: one stray, doubled, or transposed character drops a query to zero matches. When the
// direct search comes up empty, retry every single-character deletion of the query and union the hits — this
// recovers from one extra/wrong/transposed character while reusing fzf's scoring untouched.
function findTolerant(finder: FileFinder, query: string): FzfResultItem<FileRow>[] {
  const direct = finder.find(query);
  if (direct.length > 0 || query.length < TYPO_MIN_LEN) {
    return direct;
  }
  const best = new Map<FileRow, FzfResultItem<FileRow>>();
  for (let i = 0; i < query.length; i++) {
    for (const hit of finder.find(query.slice(0, i) + query.slice(i + 1))) {
      const prev = best.get(hit.item);
      if (prev === undefined || hit.score > prev.score) {
        best.set(hit.item, hit);
      }
    }
  }
  return [...best.values()];
}

// How far into the filename the match begins: 0 means the filename itself starts with the query. A match that
// begins in the directory portion sorts last — for a file search the basename is what you mean. fzf scores a
// boundary hit the same whether it's "Sess" in SessionSlot or the "sess" after the hyphen in editor-session,
// so without this tiebreak those tie and fall back to raw path length, burying the files you actually meant.
function leafOffset(item: FileRow, positions: Set<number>): number {
  let first = Number.MAX_SAFE_INTEGER;
  for (const p of positions) {
    if (p < first) {
      first = p;
    }
  }
  return first < item.leafStart ? Number.MAX_SAFE_INTEGER : first - item.leafStart;
}

/// Fuzzy-ranks the finder's files against `query` (best-first, uncapped). `recent` is most-recent-first
/// absolute paths (the open tabs). Match quality stays primary; ties then break by where the match lands
/// (filename-start beats mid-name beats directory-only), then recency, then path length — so the files you
/// meant, and the ones you're working in, surface first. Typo-tolerant: a single stray or transposed
/// character still matches.
export function rankFiles(
  finder: FileFinder,
  query: string,
  recent: readonly string[],
): ScoredFile[] {
  const rank = new Map(recent.map((abs, i) => [canonicalFsPath(abs), i] as const));
  const recencyOf = (abs: string): number =>
    rank.get(canonicalFsPath(abs)) ?? Number.MAX_SAFE_INTEGER;
  return findTolerant(finder, query)
    .sort(
      (a, b) =>
        b.score - a.score ||
        leafOffset(a.item, a.positions) - leafOffset(b.item, b.positions) ||
        recencyOf(a.item.abs) - recencyOf(b.item.abs) ||
        a.item.rel.length - b.item.rel.length,
    )
    .map((r) => ({ row: r.item, positions: r.positions }));
}
